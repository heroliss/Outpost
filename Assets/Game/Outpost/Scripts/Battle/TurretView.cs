using UnityEngine;

namespace Game.Outpost.Battle
{
    /// <summary>
    /// 玩家炮塔表现：六边形工事底座 + 中心发光核心（呼吸脉动、开火时随后坐涨亮），炮管（Pivot 子节点）指向瞄准角、
    /// 开火后坐回弹。底座换形与核心均<b>运行时程序生成</b>（六边形是运行时网格、无资产，只在 Play 生效，不改场景磁盘资产）——
    /// 让它读成"防御工事"而非一个方块。
    /// <para>朝向与开火时机全由模拟内核决定：内核按回转速度逐帧算好炮口角，导演每帧经 <see cref="Face"/> 喂给本组件；
    /// 内核只在炮口对准目标时才发 <c>EnemyHit</c>，故本组件不再自行平滑转向或判断"是否对准"——只负责把内核给的角度画出来 + 演后坐。</para>
    /// </summary>
    public sealed class TurretView : MonoBehaviour
    {
        [SerializeField, Tooltip("炮管旋转轴（绕 Z 转向目标；本地 +X 为炮口朝向）。")]
        private Transform _pivot;

        [SerializeField, Tooltip("炮管渲染体（Pivot 子节点，后坐时沿本地 -X 位移回弹）。")]
        private Transform _barrelMesh;

        [SerializeField, Tooltip("炮口挂点（Pivot 子节点，曳光的发射起点）。")]
        private Transform _muzzle;

        [SerializeField, Tooltip("单次开火的后坐位移（世界单位）。")]
        private float _recoilKick = 0.16f;

        [SerializeField, Tooltip("发光核心颜色（HDR，分量 > 1 触发 Bloom 出光晕）。")]
        private Color _coreColor = new(0.4f, 3.0f, 2.8f, 1f);

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private const float CoreBaseScale = 0.5f;

        private float _recoil;
        private float _barrelBaseX;
        private float _spin; // 射速预热系数 0..1（导演每帧喂），核心随之涨亮

        private Transform _core;
        private Material _coreMat; // 运行时创建，OnDestroy 释放
        private Material _baseMat; // 六边形工事底座的专属 Unlit 材质（运行时创建，OnDestroy 释放）
        private AudioSource[] _fireGears; // 火墙档位组（运行时挂的引擎组件，见 InitFireGears）
        private float[] _fireGearRates;   // 各档原生射速（发/秒），与烘焙资产一一对应
        private AudioSource _servoLoop; // 回转伺服电机循环（运行时挂的引擎组件，见 InitServoLoop）
        private float _sfxVolumeScale;  // 外部音量系数（主 × 音效组），SetFireWall 每帧顺带缓存——伺服层共用
        private float _lastPivotAngle;  // 上帧炮管角度（度）——伺服音从实际角度变化自测回转速度
        private float _servoSpeed;      // 平滑后的回转角速度（度/秒）

        /// <summary>曳光发射起点（炮口当前世界坐标）。</summary>
        public Vector3 MuzzleWorldPos => _muzzle.position;

        private void Awake()
        {
            _barrelBaseX = _barrelMesh.localPosition.x;
            _lastPivotAngle = _pivot.localEulerAngles.z;
            BuildEmplacement();
        }

        // 底座换六边形工事 + 中心发光核心（都程序生成、仅运行时改，不动场景资产）。
        private void BuildEmplacement()
        {
            // 无 URP 时优雅跳过（不炸）——保留原始底座外观。
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) return;

            var baseTf = transform.Find("Base");
            if (baseTf != null)
            {
                var mf = baseTf.GetComponent<MeshFilter>();
                if (mf != null) mf.sharedMesh = OutpostMeshes.Hexagon;
                baseTf.localRotation = Quaternion.identity;
                baseTf.localScale = new Vector3(2.1f, 2.1f, 1f); // 平顶六边形工事平台
                var mrBase = baseTf.GetComponent<MeshRenderer>();
                if (mrBase != null)
                {
                    // 专属枪钢灰蓝 Unlit 材质：不靠场景材质的属性名，保证平台对比青色填充盘可见（非 HDR、不发光，读成实心工事）。
                    _baseMat = new Material(shader);
                    _baseMat.SetColor(BaseColorId, new Color(0.30f, 0.34f, 0.42f, 1f));
                    mrBase.sharedMaterial = _baseMat;
                }
            }

            // 核心：贴底座中心、朝相机一侧的发光圆盘。HDR 色经 Bloom 出辉光。
            _coreMat = new Material(shader);
            _coreMat.SetColor(BaseColorId, _coreColor);

            var go = new GameObject("Core");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, -0.35f); // 炮管之前一层，核心不被炮管遮住
            go.transform.localScale = Vector3.one * CoreBaseScale;
            go.AddComponent<MeshFilter>().sharedMesh = OutpostMeshes.UnitDisc;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _coreMat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _core = go.transform;
        }

        /// <summary>把炮口摆到模拟内核给定的朝向角（度，标准数学角：0 = +X、逆时针为正；即绕本地 Z）。</summary>
        public void Face(float angleDeg) => _pivot.localRotation = Quaternion.Euler(0f, 0f, angleDeg);

        /// <summary>播放一次开火后坐。</summary>
        public void Fire() => _recoil = _recoilKick;

        /// <summary>设置炮塔核心亮度（0..1「火力热度」）：越高核心越涨亮、读作"火力拉满"，低时收拢暗淡。由导演按击发节奏自算传入。</summary>
        public void SetSpin(float spin) => _spin = Mathf.Clamp01(spin);

        /// <summary>
        /// 初始化火墙档位组（clip 与 <paramref name="nativeRates"/> 一一对应——各档是同一出膛瞬态
        /// 在不同原生射速下烘焙的连发循环，由导演经资源系统加载后传入）。跟随炮塔的<b>持续音源</b>用引擎
        /// <see cref="AudioSource"/> 组件而非框架音效池——框架刻意不替代它（§27）：逐帧调制音量 / 音高
        /// （<see cref="SetFireWall"/>）正是组件路径的地界，<c>AudioHandle</c> 不提供播放中调制。
        /// 运行时挂组件、不改场景资产（与工事底座 <see cref="BuildEmplacement"/> 同姿势）。
        /// </summary>
        public void InitFireGears(AudioClip[] clips, float[] nativeRates)
        {
            _fireGears = new AudioSource[clips.Length];
            _fireGearRates = nativeRates;
            for (int i = 0; i < clips.Length; i++)
                _fireGears[i] = CreateLoopSource(clips[i]);
        }

        /// <summary>
        /// 初始化回转伺服电机循环（clip 由导演经资源系统加载后传入）。与火墙同为跟随炮塔的持续音源、
        /// 同走引擎组件路径（见 <see cref="InitFireGears"/>）；音量 / 音高由本组件在 Update 里按
        /// 实测回转角速度自驱（<see cref="UpdateServoLoop"/>），导演无需逐帧喂值。
        /// </summary>
        public void InitServoLoop(AudioClip clip)
        {
            _servoLoop = CreateLoopSource(clip);
        }

        // 循环音源的公共装配：运行时挂组件、不改场景资产；屏幕中央的主角音源直接 2D。
        private AudioSource CreateLoopSource(AudioClip clip)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.clip = clip;
            src.loop = true;
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.volume = 0f;
            return src;
        }

        /// <summary>
        /// 单发层→火墙档位组的接棒混合系数（0=纯单发、1=纯火墙），按射速（发/秒）计算：
        /// 6 发/秒以下纯离散单响、18 发/秒以上纯火墙。曲线归本类（火墙的地界）；导演的单发层
        /// 音量取 1-blend——两侧共用一条曲线，保证接棒带内两层合计不塌陷。
        /// </summary>
        public static float HandoverBlend(float rate) => Mathf.Clamp01((rate - 6f) / 12f);

        /// <summary>
        /// 逐帧调制火墙档位组：按当前射速（发/秒，导演从击发节奏实测平滑）在相邻两档间交叉淡变、
        /// 档内变速把烘焙射速精确对齐当前射速——低速离散炮响到高速融合蜂鸣（基频=射速）的物理
        /// 连续体。选档承担重复率的大跨度（单循环宽域变速会把音色一起搬走），变速只补 ±1 档内的
        /// 细差。档权重是 log 域三角（相邻档 2 倍间隔，任意射速恰落两档之内），开方即等功率交叉；
        /// 权重为零的档 Pause 省 voice。8 发/秒以下档位组静默——那是逐发单响 sfx_shot 的地界
        /// （物理上就是离散炮响），接棒带见 <see cref="HandoverBlend"/>。
        /// 响度随射速的增长分摊两级：资产级各档 RMS 递增 +1.25dB/档（运行时 volume 上限 1.0、
        /// 火墙常年顶着 ~0.9 播，增长没法只靠标量给）+ 本方法 0.8→0.9 缓升，合计 ~6dB——
        /// 全放运行时（曾试 0.35→0.9）会在中段挖出"高射速反而变小"的音量谷。
        /// 上限 ~0.9 是全游戏最响的持续声——火墙就是这个游戏的火力幻想，必须压得住场。
        /// <paramref name="volumeScale"/> 是外部音量系数（主音量 × 音效组）：挂在对象上的组件音源
        /// 不归框架分组音量管，由业务一行乘法把它接回设置页滑条。
        /// </summary>
        public void SetFireWall(float rate, float volumeScale)
        {
            _sfxVolumeScale = Mathf.Clamp01(volumeScale); // 顺带缓存给伺服层（同一系数，见 UpdateServoLoop）
            if (_fireGears == null) return;
            // 音频表达在 384 发/秒饱和（顶档原生 256 × 变速 1.5）：射速升级无封顶，但再往上蜂鸣
            // 基频进入哨音区、响度与资产已顶满——不 clamp 则顶档权重跌出 ±1 档窗直接静音。
            // 384 以上交给视觉密度与掉帧表达"更快"。
            rate = Mathf.Min(rate, 384f);
            float blend = HandoverBlend(rate);
            float loud = 0.8f + 0.1f * Mathf.Clamp01(Mathf.Log(Mathf.Max(rate, 1f) / 16f, 2f) / 4f);
            for (int i = 0; i < _fireGears.Length; i++)
            {
                var src = _fireGears[i];
                float w = blend > 0f ? 1f - Mathf.Abs(Mathf.Log(rate / _fireGearRates[i], 2f)) : 0f;
                // 顶档在原生射速以上保持满权重（右侧没有更高档接棒三角衰减的另一半）：
                // 饱和段应"顶住"最大声，不能随权重滑落——否则 256→384 段音量反而下塌。
                if (w > 0f && i == _fireGears.Length - 1 && rate >= _fireGearRates[i]) w = 1f;
                float v = w > 0f ? Mathf.Sqrt(w) * blend * loud * _sfxVolumeScale : 0f;
                if (v <= 0.005f)
                {
                    if (src.isPlaying) src.Pause(); // Pause 而非 Stop：射速回升时从相位中段续播，无重启爆点
                    continue;
                }
                if (!src.isPlaying) src.UnPause();
                if (!src.isPlaying) src.Play(); // 首次（无暂停快照）UnPause 无效，落到 Play
                src.volume = v;
                src.pitch = rate / _fireGearRates[i]; // 权重窗 ±1 档 → pitch 天然限于 [0.5, 2]
            }
        }

        private void Update()
        {
            _recoil = Mathf.MoveTowards(_recoil, 0f, Time.deltaTime * 1.4f);
            var p = _barrelMesh.localPosition;
            p.x = _barrelBaseX - _recoil;
            _barrelMesh.localPosition = p;

            UpdateServoLoop(Time.deltaTime);

            // 核心呼吸脉动；开火后坐未回落时随之涨大一点，呼应"刚开了一炮"。
            if (_core != null)
            {
                float breath = 1f + 0.12f * Mathf.Sin(Time.time * 4f);
                float kick = 1f + _recoil * 1.5f;
                float spin = 1f + 0.5f * _spin; // 预热拉满时核心涨大半圈
                _core.localScale = Vector3.one * (CoreBaseScale * breath * kick * spin);
            }
        }

        // 回转伺服音：从 pivot 实际角度变化自测角速度并平滑，驱动电机循环的音量 / 音高。
        // 追踪目标的小幅微调（几度/秒）落在静音阈值下，只有换目标的大摆头（内核回转 140~360 度/秒）
        // 读成"电机甩转"——伺服音是「炮塔在调头」的听觉信号，不是常驻底噪。
        private void UpdateServoLoop(float dt)
        {
            if (_servoLoop == null || dt <= 0f) return;

            float ang = _pivot.localEulerAngles.z;
            float inst = Mathf.Abs(Mathf.DeltaAngle(_lastPivotAngle, ang)) / dt;
            _lastPivotAngle = ang;
            _servoSpeed = Mathf.Lerp(_servoSpeed, inst, 1f - Mathf.Exp(-10f * dt)); // 指数平滑：起转快、停转留短余韵

            float norm = Mathf.InverseLerp(30f, 320f, _servoSpeed); // 30 度/秒以下静音（追踪微调），320 度/秒拉满
            float v = norm * 0.38f * _sfxVolumeScale;
            if (v <= 0.005f)
            {
                if (_servoLoop.isPlaying) _servoLoop.Pause(); // 同火墙层：Pause 保相位，再动时从中段续播无重启爆点
                return;
            }
            if (!_servoLoop.isPlaying) _servoLoop.UnPause();
            if (!_servoLoop.isPlaying) _servoLoop.Play(); // 首次（无暂停快照）UnPause 无效，落到 Play
            _servoLoop.volume = v;
            _servoLoop.pitch = 0.8f + 0.6f * norm; // 甩得越快电机音越尖
        }

        private void OnDestroy()
        {
            if (_coreMat != null) Destroy(_coreMat);
            if (_baseMat != null) Destroy(_baseMat);
        }
    }
}
