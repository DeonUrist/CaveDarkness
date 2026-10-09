// CaveDarkness: per-cave darkness maps for the terrain. The plugin bakes a small 2D map (light 0..1, R channel) over the floor of each cave
// and hands up to 4 of them (the caves nearest the camera) to the shader as globals. Only the AMBIENT part of the lighting (sky light +
// reflections) is scaled; the sun and every point/spot light (flashlight, fires, lamps) light the cave floor as before.
#ifndef CAVEDARKNESS_INCLUDED
#define CAVEDARKNESS_INCLUDED

sampler2D _CaveMap0, _CaveMap1, _CaveMap2, _CaveMap3;
float4 _CaveRect0, _CaveRect1, _CaveRect2, _CaveRect3;   // xy = world x/z of the map's corner, zw = 1 / size
float _CaveCount;

half CaveSample(sampler2D m, float4 r, float3 w)
{
    float2 uv = (w.xz - r.xy) * r.zw;
    if (uv.x < 0 || uv.y < 0 || uv.x > 1 || uv.y > 1) return 1;
    return tex2Dlod(m, float4(uv, 0, 0)).r;
}

half CaveAmbient(float3 w)
{
    half a = 1;
    if (_CaveCount > 0.5) a = min(a, CaveSample(_CaveMap0, _CaveRect0, w));
    if (_CaveCount > 1.5) a = min(a, CaveSample(_CaveMap1, _CaveRect1, w));
    if (_CaveCount > 2.5) a = min(a, CaveSample(_CaveMap2, _CaveRect2, w));
    if (_CaveCount > 3.5) a = min(a, CaveSample(_CaveMap3, _CaveRect3, w));
    return a;
}

inline half4 LightingCaveStandard(SurfaceOutputStandard s, float3 viewDir, UnityGI gi)
{
    return LightingStandard(s, viewDir, gi);
}

inline void LightingCaveStandard_GI(SurfaceOutputStandard s, UnityGIInput data, inout UnityGI gi)
{
    LightingStandard_GI(s, data, gi);
    half a = CaveAmbient(data.worldPos);
    gi.indirect.diffuse *= a;
    gi.indirect.specular *= a;
}

#endif
