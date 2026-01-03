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

        // [新增] 智能 Bug 标记：用于记录当前的濒死/昏迷是否是卡住的假数据
        private bool _isDeathTimerBugged = false;
        private bool _isPassedOutBugged = false;

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
            components.CacheGameComponents();

            // 总开关与暂停检测
            if (!_config.EnableShock.Value || Time.timeScale <= 0f)
            {
                if (CurrentFinalStrength > 0f)
                {
                    CurrentFinalStrength = 0f;
                    _ = _apiClient.SendStrengthUpdateAsync(set: 0);
                }
                return;
            }

            if ((DateTime.UtcNow - _lastUpdateTimer).TotalMilliseconds < _config.CheckIntervalMs.Value)
                return;
            _lastUpdateTimer = DateTime.UtcNow;

            Character player = _config.EnableSpectatorShock.Value ? Character.observedCharacter : Character.localCharacter;

            if (player == null || player.data == null)
            {
                if (CurrentFinalStrength > 0f)
                {
                    CurrentFinalStrength = 0f;
                    _ = _apiClient.SendStrengthUpdateAsync(set: 0);
                }
                _lastEnergy = null;
                _energyLossAccumulator = 0f;
                return;
            }

            var data = player.data;
            UpdateEnergyTracker(data);

            // =========================
            // [新增] 智能 Bug 识别逻辑
            // =========================
            // 如果数据显示异常(濒死/昏迷)，但玩家身体控制力很高(>0.8，说明站起来了)
            // 那么立刻标记这些状态为 Bug，后续直接无视。
            if (data.currentRagdollControll > 0.8f)
            {
                if (data.deathTimer > 0f && !_isDeathTimerBugged)
                {
                    _isDeathTimerBugged = true;
                    _logger.LogInfo("[System] 检测到 '濒死诈尸' (DeathTimer卡住)，已自动忽略异常状态。");
                }
                if ((data.passedOut || data.fullyPassedOut) && !_isPassedOutBugged)
                {
                    _isPassedOutBugged = true;
                    _logger.LogInfo("[System] 检测到 '昏迷行走' (PassedOut卡住)，已自动忽略异常状态。");
                }
            }

            // 状态恢复检测：如果游戏数据真的归零了，那把 Bug 标记也清掉，以便下次正常触发
            if (data.deathTimer <= 0f) _isDeathTimerBugged = false;
            if (!data.passedOut && !data.fullyPassedOut) _isPassedOutBugged = false;

            // ID 变化检测 (换人了重置标记)
            int currentId = player.GetInstanceID();
            if (currentId != _lastCharacterId)
            {
                _lastCharacterId = currentId;
                _ignoreStaminaShock = true;
                _protectionStartTime = Time.unscaledTime;
                _energyLossAccumulator = 0f;
                _lastEnergy = data.extraStamina;
                _isDeathTimerBugged = false;
                _isPassedOutBugged = false;
            }

            // ===== 1. 真正死亡 =====
            if (data.dead)
            {
                ProcessDeathState(data);
                return;
            }

            // 复活逻辑
            if (_isDead && !data.dead)
            {
                _isDead = false;
                _ignoreStaminaShock = true;
                _protectionStartTime = Time.unscaledTime;
                CurrentFinalStrength = 0f;
                _ = _apiClient.SendStrengthUpdateAsync(set: 0);
                _energyLossAccumulator = 0f;
                _lastEnergy = data.extraStamina;
                _isDeathTimerBugged = false;
                _isPassedOutBugged = false;
            }

            // ===== 2. 濒死保护 (DeathTimer) =====
            // [修复] 只有当 deathTimer > 0 且 [不是Bug] 且 [身体瘫软] 时，才认为是真濒死。
            if (data.deathTimer > 0f && !_isDeathTimerBugged && data.currentRagdollControll < 0.5f)
            {
                if (CurrentFinalStrength != 0f)
                {
                    CurrentFinalStrength = 0f;
                    _ = _apiClient.SendStrengthUpdateAsync(set: 0);
                }
                return;
            }

            // 智能体力保护
            if (_ignoreStaminaShock)
            {
                float stamina = Mathf.Clamp01(data.currentStamina);
                if (stamina > 0.95f || (Time.unscaledTime - _protectionStartTime > 15.0f))
                {
                    _ignoreStaminaShock = false;
                }
                else
                {
                    if (CurrentFinalStrength > 0f)
                    {
                        CurrentFinalStrength = 0f;
                        _ = _apiClient.SendStrengthUpdateAsync(set: 0);
                    }
                    return;
                }
            }

            // ===== 3. 昏迷逻辑 (PassedOut) =====
            // [修复] 同样增加智能判定
            if ((data.passedOut || data.fullyPassedOut) && !_isPassedOutBugged && data.currentRagdollControll < 0.5f)
            {
                float baseVal = CalculateBaseStatus(data, player.refs?.afflictions, false);
                float target = baseVal * _config.PassOutMultiplier.Value;
                CurrentFinalStrength = target < 0.01f ? 0f : target;

                int sendVal = Mathf.CeilToInt(CurrentFinalStrength);
                _ = _apiClient.SendStrengthUpdateAsync(set: sendVal);

                _energyLossAccumulator = 0f;
                return;
            }

            // ===== 4. 正常生存状态 =====
            if (player.refs?.afflictions == null) return;

            float activeStrength = CalculateBaseStatus(data, player.refs.afflictions, _ignoreStaminaShock);

            // [修复] 摔倒逻辑：叠加模式 (修复摔倒被覆盖的问题)
            if (data.fallSeconds > 0.1f)
            {
                activeStrength += _config.FallPunishment.Value;
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
                    _logger.LogInfo($"[Alive] 目标:{activeStrength:F1} 输出:{CurrentFinalStrength:F1}");
                }
            }

            int finalSend = Mathf.CeilToInt(CurrentFinalStrength);
            _ = _apiClient.SendStrengthUpdateAsync(set: finalSend);

            ProcessEnergyPulse();
        }

        private void UpdateEnergyTracker(CharacterData data)
        {
            if (_lastEnergy == null) _lastEnergy = data.extraStamina;
        }

        private void ProcessDeathState(CharacterData data)
        {
            if (!_isDead)
            {
                _isDead = true;
                _deathStartTime = Time.unscaledTime;
            }

            float elapsed = Time.unscaledTime - _deathStartTime;
            float target = 0f;

            if (elapsed < _config.DeathDuration.Value)
                target = _config.DeathPunishment.Value;

            if (target >= CurrentFinalStrength)
                CurrentFinalStrength = target;
            else
                CurrentFinalStrength = Mathf.Max(target, CurrentFinalStrength - _config.ReductionValue.Value);

            int sendVal = Mathf.CeilToInt(CurrentFinalStrength);
            _ = _apiClient.SendStrengthUpdateAsync(set: sendVal);
        }

        private void ApplySmoothing(float target)
        {
            if (target >= CurrentFinalStrength)
                CurrentFinalStrength = target;
            else
                CurrentFinalStrength = Mathf.Max(target, CurrentFinalStrength - _config.ReductionValue.Value);
        }

        private void ProcessEnergyPulse()
        {
            var player = _config.EnableSpectatorShock.Value ? Character.observedCharacter : Character.localCharacter;
            if (player?.data == null) return;
            var data = player.data;

            // [修复] 脉冲也遵循智能判定：只有真正瘫软时才阻止脉冲
            bool isRealDying = (data.deathTimer > 0f && !_isDeathTimerBugged && data.currentRagdollControll < 0.5f);
            bool isRealPassedOut = ((data.passedOut || data.fullyPassedOut) && !_isPassedOutBugged && data.currentRagdollControll < 0.5f);

            if (isRealDying || isRealPassedOut)
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