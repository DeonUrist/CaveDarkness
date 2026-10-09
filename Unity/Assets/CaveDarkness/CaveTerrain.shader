// Copy of Unity's built-in "Nature/Terrain/Standard" (forward only) whose ambient light follows CaveDarkness's cave maps.
Shader "CaveDarkness/Terrain" {
    Properties {
        [HideInInspector] _MainTex ("BaseMap (RGB)", 2D) = "white" {}
        [HideInInspector] _Color ("Main Color", Color) = (1,1,1,1)
        [HideInInspector] _TerrainHolesTexture("Holes Map (RGB)", 2D) = "white" {}
    }

    SubShader {
        Tags {
            "Queue" = "Geometry-100"
            "RenderType" = "Opaque"
            "TerrainCompatible" = "True"
        }

        CGPROGRAM
        #pragma surface surf CaveStandard vertex:SplatmapVert finalcolor:SplatmapFinalColor addshadow fullforwardshadows exclude_path:deferred exclude_path:prepass nolightmap nodynlightmap nodirlightmap
        #pragma instancing_options assumeuniformscaling nomatrices nolightprobe nolightmap forwardadd
        #pragma multi_compile_fog
        #pragma target 3.0
        #include "UnityPBSLighting.cginc"

        #pragma multi_compile_local __ _ALPHATEST_ON
        #pragma multi_compile_local __ _NORMALMAP

        #define TERRAIN_STANDARD_SHADER
        #define TERRAIN_INSTANCED_PERPIXEL_NORMAL
        #define TERRAIN_SURFACE_OUTPUT SurfaceOutputStandard
        #include "TerrainSplatmapCommon.cginc"
        #include "CaveDarkness.cginc"

        half _Metallic0;
        half _Metallic1;
        half _Metallic2;
        half _Metallic3;

        half _Smoothness0;
        half _Smoothness1;
        half _Smoothness2;
        half _Smoothness3;

        void surf (Input IN, inout SurfaceOutputStandard o) {
            half4 splat_control;
            half weight;
            fixed4 mixedDiffuse;
            half4 defaultSmoothness = half4(_Smoothness0, _Smoothness1, _Smoothness2, _Smoothness3);
            SplatmapMix(IN, defaultSmoothness, splat_control, weight, mixedDiffuse, o.Normal);
            o.Albedo = mixedDiffuse.rgb;
            o.Alpha = weight;
            o.Smoothness = mixedDiffuse.a;
            o.Metallic = dot(splat_control, half4(_Metallic0, _Metallic1, _Metallic2, _Metallic3));
        }
        ENDCG
    }

    Dependency "AddPassShader"    = "CaveDarkness/Terrain-AddPass"
    Dependency "BaseMapShader"    = "Hidden/TerrainEngine/Splatmap/Standard-Base"
    Dependency "BaseMapGenShader" = "Hidden/TerrainEngine/Splatmap/Standard-BaseGen"

    Fallback "Nature/Terrain/Diffuse"
}
