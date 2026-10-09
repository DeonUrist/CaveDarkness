// CaveDarkness: per-cave darkness maps for the terrain. The plugin bakes a small 2D map (light 0..1, R channel) over the floor of each cave
// and hands up to 4 of them (the caves nearest the camera) to the shader as globals. Only the AMBIENT part of the lighting (sky light +
// reflections) is scaled; the sun and every point/spot light (flashlight, fires, lamps) light the cave floor as before.
#ifndef CAVEDARKNESS_INCLUDED
#define CAVEDARKNESS_INCLUDED

// One sampler for all four maps: the terrain shader already uses most of d3d11's 16 sampler slots.
UNITY_DECLARE_TEX2D(_CaveMap0);
UNITY_DECLARE_TEX2D_NOSAMPLER(_CaveMap1);
UNITY_DECLARE_TEX2D_NOSAMPLER(_CaveMap2);
UNITY_DECLARE_TEX2D_NOSAMPLER(_CaveMap3);
float4 _CaveRect0, _CaveRect1, _CaveRect2, _CaveRect3;   // xy = world x/z of the map's corner, zw = 1 / size
float _CaveCount;

// 1 outside the map's rectangle (sampled anyway, no branch: keeps the gradients valid)
#define CAVE_SAMPLE(CM_TEX, CM_RECT, CM_W, CM_OUT) { float2 cmUv = (CM_W.xz - CM_RECT.xy) * CM_RECT.zw; \
    half cmIn = step(0, cmUv.x) * step(0, cmUv.y) * step(cmUv.x, 1) * step(cmUv.y, 1); \
    half cmV = UNITY_SAMPLE_TEX2D_SAMPLER(CM_TEX, _CaveMap0, saturate(cmUv)).x; \
    CM_OUT = min(CM_OUT, lerp(1, cmV, cmIn)); }

half CaveAmbient(float3 w)
{
    half a = 1;
    half a0 = 1, a1 = 1, a2 = 1, a3 = 1;
    CAVE_SAMPLE(_CaveMap0, _CaveRect0, w, a0)
    CAVE_SAMPLE(_CaveMap1, _CaveRect1, w, a1)
    CAVE_SAMPLE(_CaveMap2, _CaveRect2, w, a2)
    CAVE_SAMPLE(_CaveMap3, _CaveRect3, w, a3)
    if (_CaveCount > 0.5) a = min(a, a0);
    if (_CaveCount > 1.5) a = min(a, a1);
    if (_CaveCount > 2.5) a = min(a, a2);
    if (_CaveCount > 3.5) a = min(a, a3);
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
    // The sun too (the GI function only runs in the base pass, so the flashlight, fires and lamps in the forward-add passes are untouched):
    // the rock shells don't reach into the ground, and the low sun leaks through the gap / shadow bias at the foot of the walls onto a
    // strip of floor. Full sun where the map says open (the mouth), none from half darkness inward.
    gi.light.color *= saturate(a * 2);
}

#endif
