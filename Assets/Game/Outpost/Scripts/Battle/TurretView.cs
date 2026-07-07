using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 玩家炮塔表现：底座固定，炮管（Pivot 子节点）平滑转向瞄准目标，开火时炮管后坐回弹。
    /// 纯表现组件——瞄谁、何时开火全由 <see cref="BattleDirector"/> 驱动（模拟内核是 hitscan，本组件只负责"演"）。
    /// </summary>
    public sealed class TurretView : MonoBehaviour
    {
        [SerializeField, Tooltip("炮管旋转轴（绕 Z 转向目标；本地 +X 为炮口朝向）。")]
        private Transform _pivot;

        [SerializeField, Tooltip("炮管渲染体（Pivot 子节点，后坐时沿本地 -X 位移回弹）。")]
        private Transform _barrelMesh;

        [SerializeField, Tooltip("炮口挂点（Pivot 子节点，曳光的发射起点）。")]
        private Transform _muzzle;

        [SerializeField, Tooltip("转向速度（度/秒）。")]
        private float _turnSpeed = 720f;

        [SerializeField, Tooltip("单次开火的后坐位移（世界单位）。")]
        private float _recoilKick = 0.16f;

        private float _targetAngle;
        private float _currentAngle;
        private float _recoil;
        private float _barrelBaseX;

        /// <summary>曳光发射起点（炮口当前世界坐标）。</summary>
        public Vector3 MuzzleWorldPos => _muzzle.position;

        private void Awake()
        {
            _barrelBaseX = _barrelMesh.localPosition.x;
            _currentAngle = _targetAngle = _pivot.localEulerAngles.z;
        }

        /// <summary>把炮口转向世界坐标目标（平滑追踪，不瞬转）。</summary>
        public void AimAt(Vector3 worldPos)
        {
            var d = worldPos - _pivot.position;
            if (d.sqrMagnitude < 0.0001f) return;
            _targetAngle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        }

        /// <summary>播放一次开火后坐。</summary>
        public void Fire() => _recoil = _recoilKick;

        private void Update()
        {
            _currentAngle = Mathf.MoveTowardsAngle(_currentAngle, _targetAngle, _turnSpeed * Time.deltaTime);
            _pivot.localRotation = Quaternion.Euler(0f, 0f, _currentAngle);

            _recoil = Mathf.MoveTowards(_recoil, 0f, Time.deltaTime * 1.4f);
            var p = _barrelMesh.localPosition;
            p.x = _barrelBaseX - _recoil;
            _barrelMesh.localPosition = p;
        }
    }
}
