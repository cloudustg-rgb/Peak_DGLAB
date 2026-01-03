using System;
using UnityEngine;
using BepInEx.Logging;

namespace PeakDGLab
{
    public class PlayerStatusController
    {
        private readonly ConfigManager _config;
        private readonly DGLabApiClient _apiClient;
        private readonly ManualLogSource _logger;

        private float? _lastEnergy = null;
        private float _energyLossAccumulator = 0f;
        private DateTime _lastUpdateTimer;
        private float _debugLogTimer = 0f;

        // 死亡状态机
        private bool _isDead = false;
        private float _deathStartTime = 0f;

        // 智能保护变量
        private bool _ignoreStaminaShock = true;
        private float _protectionStartTime = 0f;
        private int _lastCharacterId = -1;

        public static float CurrentFinalStrength = 0f;

        public PlayerStatusController(
            ConfigManager config,
            DGLabApiClient apiClient,
            ManualLogSource logger
        )
        {
            _config = config;
            _apiClient = apiClient;
            _logger = logger;
            _lastUpdateTimer = DateTime.UtcNow;
            _protectionStartTime = Time.unscaledTime;
        }

        public void ProcessPlayerStatusUpdate(GameComponentManager components)
        {
            // 1. 刷新组件缓存 (不管能不能拿到，先刷新)
            components.CacheGameComponents();

            // 2. 总开关检测 (最高优先级：硬断电)
            if (!_config.EnableShock.Value)
            {
                if (CurrentFinalStrength > 0f)
                {
                    CurrentFinalStrength = 0f;
                    _ = _apiClient.SendStrengthUpdateAsync(set: 0);
                }
                return;
            }

            // 3. [拆分逻辑 A] 游戏暂停检测 (单机ESC/暂停)
            // Time.timeScale 为 0 表示游戏世界停止了。
            // 此时应立即停止输出防止"卡震"，但保留内部状态(如能量累积)，以便取消暂停时无缝衔接。
            // 注意：联机模式按 ESC 通常 timeScale 仍为 1，不会进入这里，符合"联机不停"的需求。
            if (Time.timeScale <= 0f)
            {
                if (CurrentFinalStrength > 0f)
                {
                    CurrentFinalStrength = 0f;
                    _ = _apiClient.SendStrengthUpdateAsync(set: 0);
                }
                return; // 暂停时直接返回，不跑后续逻辑
            }

            // 4. 频率限制
            if ((DateTime.UtcNow - _lastUpdateTimer).TotalMilliseconds < _config.CheckIntervalMs.Value)
                return;
            _lastUpdateTimer = DateTime.UtcNow;

            // 5. 目标获取 (处理 观战 vs 本体)
            Character player;
            if (_config.EnableSpectatorShock.Value)
                player = Character.observedCharacter; // 自动处理: 没观战时它就是 localCharacter
            else
                player = Character.localCharacter;

            // 6. [拆分逻辑 B] 存在性检测 (主菜单/加载中/未生成)
            // 如果这个时候 player 是空的，说明回到了主菜单或正在换场景。
            // 此时必须执行"彻底归零"和"状态重置"。
            if (player == null || player.data == null)
            {
                if (CurrentFinalStrength > 0f)
                {
                    CurrentFinalStrength = 0f;
                    _ = _apiClient.SendStrengthUpdateAsync(set: 0);
                }

                // 彻底丢失目标时，重置所有监测状态，防止重新进游戏瞬间误判
                _lastEnergy = null;
                _energyLossAccumulator = 0f;
                _lastCharacterId = -1;
                return;
            }

            // === 至此，确认游戏正在进行(timeScale>0) 且 角色存在(player!=null) ===
            var data = player.data;

            // [同步能量基准] 
            UpdateEnergyTracker(data);

            // [ID变化检测] (重生/切换观战)
            int currentId = player.GetInstanceID();
            if (currentId != _lastCharacterId)
            {
                _lastCharacterId = currentId;
                _ignoreStaminaShock = true;
                _protectionStartTime = Time.unscaledTime;
                _logger.LogInfo("[System] 角色切换/重生，开启体力屏蔽保护");

                // 切换角色，清空累积池，重置能量基准
                _energyLossAccumulator = 0f;
                _lastEnergy = data.extraStamina;
            }

            // ===== 死亡判定 =====
            if (data.dead)
            {
                ProcessDeathState(data);
                return;
            }

            // 死亡状态释放 (复活)
            if (_isDead && !data.dead)
            {
                _isDead = false;
                _ignoreStaminaShock = true;
                _protectionStartTime = Time.unscaledTime;

                CurrentFinalStrength = 0f;
                _ = _apiClient.SendStrengthUpdateAsync(set: 0);
                _logger.LogInfo("[Death] 玩家已复活，开启体力屏蔽保护");

                // 复活重置能量
                _energyLossAccumulator = 0f;
                _lastEnergy = data.extraStamina;
            }

            // 濒死阶段
            if (data.deathTimer > 0f)
            {
                if (CurrentFinalStrength != 0f)
                {
                    CurrentFinalStrength = 0f;
                    _ = _apiClient.SendStrengthUpdateAsync(set: 0);
                }
                _energyLossAccumulator = 0f;
                return;
            }

            // 智能保护解除检查
            if (_ignoreStaminaShock)
            {
                float stamina = Mathf.Clamp01(data.currentStamina);
                // 解除条件: 体力>95% 或 保护超时15秒
                if (stamina > 0.95f || (Time.unscaledTime - _protectionStartTime > 15.0f))
                {
                    _ignoreStaminaShock = false;
                    _logger.LogInfo($"[System] 体力屏蔽保护解除 (体力:{stamina:P0})");
                }
                else
                {
                    // 保护期：如果有震动输出则归零 (但允许能量监测在后台跑，只是不发)
                    if (CurrentFinalStrength > 0f)
                    {
                        CurrentFinalStrength = 0f;
                        _ = _apiClient.SendStrengthUpdateAsync(set: 0);
                    }
                    _energyLossAccumulator = 0f; // 保护期不累积能量震动
                    return;
                }
            }

            // ===== 昏迷 =====
            if (data.passedOut || data.fullyPassedOut)
            {
                // 昏迷不是"低体力"，所以传入 false (不屏蔽)
                float baseVal = CalculateBaseStatus(data, player.refs?.afflictions, false);
                float target = baseVal * _config.PassOutMultiplier.Value;

                CurrentFinalStrength = target < 0.01f ? 0f : target;

                int sendVal = Mathf.CeilToInt(CurrentFinalStrength);
                _ = _apiClient.SendStrengthUpdateAsync(set: sendVal);

                _energyLossAccumulator = 0f;
                return;
            }

            // ===== 正常生存状态 =====
            if (player.refs?.afflictions == null)
            {
                if (CurrentFinalStrength > 0f)
                {
                    CurrentFinalStrength = 0f;
                    _ = _apiClient.SendStrengthUpdateAsync(set: 0);
                }
                return;
            }

            float activeStrength = CalculateBaseStatus(data, player.refs.afflictions, _ignoreStaminaShock);
            if (data.fallSeconds > 0.1f)
            {
                // 叠加摔倒惩罚强度
                activeStrength += _config.FallPunishment.Value;

                // 摔倒期间依然需要清空能量累积，防止物理碰撞导致SP误判
                _energyLossAccumulator = 0f;
                _lastEnergy = data.extraStamina;
            }
            ApplySmoothing(activeStrength);

            if (_config.EnableDebugLog.Value)
            {
                _debugLogTimer += Time.unscaledDeltaTime;
                if (_debugLogTimer >= 1f)
                {
                    _debugLogTimer = 0f;
                    string protectInfo = _ignoreStaminaShock ? "[护]" : "";
                    _logger.LogInfo($"[Alive]{protectInfo} 目标:{activeStrength:F1} 输出:{CurrentFinalStrength:F1}");
                }
            }

            int finalSend = Mathf.CeilToInt(CurrentFinalStrength);
            _ = _apiClient.SendStrengthUpdateAsync(set: finalSend);

            // 处理能量脉冲
            ProcessEnergyPulse();
        }

        private void UpdateEnergyTracker(CharacterData data)
        {
            if (_lastEnergy == null)
            {
                _lastEnergy = data.extraStamina;
            }
        }

        private void ProcessDeathState(CharacterData data)
        {
            if (!_isDead)
            {
                _isDead = true;
                _deathStartTime = Time.unscaledTime;
                _logger.LogInfo("[Death] 玩家死亡，惩罚开始");
            }

            float elapsed = Time.unscaledTime - _deathStartTime;
            float target = 0f;

            if (elapsed < _config.DeathDuration.Value)
            {
                target = _config.DeathPunishment.Value;
            }

            if (target >= CurrentFinalStrength)
                CurrentFinalStrength = target;
            else
                CurrentFinalStrength = Mathf.Max(
                    target,
                    CurrentFinalStrength - _config.ReductionValue.Value
                );

            int sendVal = Mathf.CeilToInt(CurrentFinalStrength);
            _ = _apiClient.SendStrengthUpdateAsync(set: sendVal);
        }

        private void ApplySmoothing(float target)
        {
            if (target >= CurrentFinalStrength)
            {
                CurrentFinalStrength = target;
            }
            else
            {
                CurrentFinalStrength = Mathf.Max(
                    target,
                    CurrentFinalStrength - _config.ReductionValue.Value
                );
            }
        }

        private void ProcessEnergyPulse()
        {
            var player = _config.EnableSpectatorShock.Value ? Character.observedCharacter : Character.localCharacter;
            if (player?.data == null) return;
            var data = player.data;

            if (data.passedOut || data.fullyPassedOut)
            {
                _energyLossAccumulator = 0f;
                _lastEnergy = data.extraStamina;
                return;
            }

            float currentEnergy = data.extraStamina;

            if (_lastEnergy != null)
            {
                float diff = _lastEnergy.Value - currentEnergy;

                if (diff > 0)
                {
                    _energyLossAccumulator += diff;
                    int pulseStrength = Mathf.FloorToInt(_energyLossAccumulator * _config.EnergyLossMultiplier.Value);

                    if (pulseStrength >= 3)
                    {
                        if (_config.EnableDebugLog.Value)
                            _logger.LogInfo($"[Pulse] SP消耗触发: 强度 {pulseStrength} (累积 {_energyLossAccumulator:F4})");

                        _ = _apiClient.SendStrengthUpdateAsync(add: pulseStrength);
                        _energyLossAccumulator = 0f;
                    }
                }
                else if (diff < 0)
                {
                    _energyLossAccumulator = 0f;
                }
            }
            _lastEnergy = currentEnergy;
        }

        private float CalculateBaseStatus(CharacterData data, CharacterAfflictions afflictions, bool ignoreStamina = false)
        {
            if (data == null || afflictions == null) return 0f;

            float strength = 0f;

            if (!ignoreStamina)
            {
                float stamina = Mathf.Clamp01(data.currentStamina);
                if (stamina < 1f)
                {
                    float loss = 1f - stamina;
                    float power = Mathf.Max(0.1f, _config.StaminaCurvePower.Value);
                    strength += Mathf.Pow(loss, power) * _config.LowStaminaMaxStrength.Value;
                }
            }

            strength += afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Drowsy) * _config.WeightDrowsy.Value;
            strength += afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Cold) * _config.WeightCold.Value;
            strength += afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Hot) * _config.WeightHot.Value;
            strength += afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Poison) * _config.WeightPoison.Value;
            strength += afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Thorns) * _config.WeightThorns.Value;
            strength += afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Curse) * _config.WeightCurse.Value;
            strength += afflictions.GetCurrentStatus(CharacterAfflictions.STATUSTYPE.Hunger) * _config.WeightHunger.Value;

            return strength;
        }
    }
}
