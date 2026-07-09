// 敌人海实例化渲染专用的最小无光照 shader：
// 每实例颜色（UNITY_DEFINE_INSTANCED_PROP）——URP 自带 Unlit 不支持 per-instance 色，故手写。
// 无 LightMode 标签的 pass 在 URP 下按 SRPDefaultUnlit 渲染；输出 float4 保留 HDR 分量（>1 触发 Bloom 辉光）。
// Cull Off：顶视程序网格双面可见（与 OutpostMeshes 双面化同一动机，双保险）。
Shader "Outpost/SwarmUnlit"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            Cull Off
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            // 编辑器同步编译：DrawMeshInstanced 不会触发异步 shader 编译（也没有 MeshRenderer 兜底），
            // 异步模式下实例化绘制会被静默跳过、敌人海整体不可见，直到有别的渲染器用到本 shader。
            #pragma editor_sync_compilation
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(float4, _BaseColor)
            UNITY_INSTANCING_BUFFER_END(Props)

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                return UNITY_ACCESS_INSTANCED_PROP(Props, _BaseColor);
            }
            ENDCG
        }
    }
}
