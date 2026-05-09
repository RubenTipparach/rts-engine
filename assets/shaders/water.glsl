// Water shader (GLSL port of water.wgsl). See the WGSL file for the design
// notes — Catlike Coding "Looking Through Water" port: the underwater term
// is the depth-difference fog over a refracted scene snapshot; surface
// highlights stay from the legacy shader so the water keeps its aesthetic.

layout(std140, binding = 0) uniform U {
    mat4 mvp;
    vec4 sunDir;
    vec4 cameraPos;
    // params: time, fogDensity, refractionStrength, reserved
    vec4 params;
    // fogColor.rgb = waterFogColor
    vec4 fogColor;
    // viewport: width, height, 1/width, 1/height
    vec4 viewport;
} u;

layout(binding = 2) uniform sampler2D waterDuDv;
layout(binding = 4) uniform sampler2D sceneColor;
layout(binding = 5) uniform sampler2D sceneDepth;
// Sampler bindings 1 and 3 are bound by the OpenGL backend onto texture
// units 2 and 4 respectively (per-texture sampler entries in the bind
// group) so the DuDv map gets repeat filtering and sceneColor gets clamp
// filtering. sceneDepth is read via texelFetch and ignores the sampler.

const float LOG_DEPTH_FAR = 10000.0;
vec4 applyLogDepth(vec4 p) {
    float logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4(p.x, p.y, logZ * p.w, p.w);
}
float linearizeLogDepth(float ndcZ) {
    return pow(1.0 + LOG_DEPTH_FAR, ndcZ) - 1.0;
}

#ifdef VERTEX
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
out vec3 vWorldPos;
out vec3 vNormal;

void main() {
    gl_Position = applyLogDepth(u.mvp * vec4(aPos, 1.0));
    vWorldPos = aPos;
    vNormal = normalize(aNormal);
}
#endif

#ifdef FRAGMENT
in vec3 vWorldPos;
in vec3 vNormal;
out vec4 FragColor;

void main() {
    vec3 N = normalize(vNormal);
    vec3 L = normalize(u.sunDir.xyz);
    vec3 V = normalize(u.cameraPos.xyz - vWorldPos);
    float t = u.params.x;
    float fogDensity = u.params.y;
    float refractionStrength = u.params.z;

    // Triplanar UV.
    vec3 absN = max(abs(N), vec3(0.001));
    float total = absN.x + absN.y + absN.z;
    float wx = absN.x / total;
    float wy = absN.y / total;
    float wz = absN.z / total;
    float tiling = 6.0;
    vec2 waterUV;
    if (wy > wx && wy > wz)      waterUV = vWorldPos.xz * tiling;
    else if (wx > wz)            waterUV = vWorldPos.zy * tiling;
    else                         waterUV = vWorldPos.xy * tiling;

    float moveSpeed = 0.03;
    float moveFactor = t * moveSpeed;
    vec2 dudvUV1 = vec2(waterUV.x + moveFactor, waterUV.y);
    vec2 dudv1 = textureLod(waterDuDv, dudvUV1, 0.0).rg * 0.1;
    vec2 dudvUV2 = waterUV + vec2(dudv1.x, dudv1.y + moveFactor);
    vec2 dudv2 = textureLod(waterDuDv, dudvUV2, 0.0).rg * 2.0 - vec2(1.0);

    vec3 tang = cross(N, vec3(0.0, 1.0, 0.0));
    if (dot(tang, tang) < 0.01) tang = cross(N, vec3(1.0, 0.0, 0.0));
    tang = normalize(tang);
    vec3 bitang = normalize(cross(N, tang));
    vec3 waveN = normalize(N + tang * dudv2.x * 0.3 + bitang * dudv2.y * 0.3);

    // Screen-space depth difference. Depth is read via texelFetch (no
    // sampler) so the colour binding's filtering linear sampler is unaffected.
    vec2 invViewport = u.viewport.zw;
    ivec2 viewportSize = ivec2(u.viewport.xy);
    ivec2 viewportMax = viewportSize - ivec2(1);
    ivec2 pixelCoord0 = clamp(ivec2(gl_FragCoord.xy), ivec2(0), viewportMax);
    vec2 screenUV0 = gl_FragCoord.xy * invViewport;

    float surfaceEye = linearizeLogDepth(gl_FragCoord.z);
    float bgNdcZ0 = texelFetch(sceneDepth, pixelCoord0, 0).r;
    float bgEye0  = linearizeLogDepth(bgNdcZ0);
    float depthDiff0 = max(0.0, bgEye0 - surfaceEye);

    // Refraction.
    float aspect = u.viewport.x * invViewport.y;
    vec2 uvOffset = vec2(dudv2.x, dudv2.y) * refractionStrength;
    uvOffset.y *= aspect;
    uvOffset *= clamp(depthDiff0, 0.0, 1.0);
    vec2 refractedUV = clamp(screenUV0 + uvOffset, vec2(0.0), vec2(1.0));

    ivec2 pixelCoordR = clamp(ivec2(refractedUV * u.viewport.xy), ivec2(0), viewportMax);
    float bgNdcZ = texelFetch(sceneDepth, pixelCoordR, 0).r;
    float bgEye  = linearizeLogDepth(bgNdcZ);
    float diff = bgEye - surfaceEye;
    vec2 bgUV = refractedUV;
    if (diff < 0.0) {
        bgUV = screenUV0;
        diff = depthDiff0;
    }

    vec3 bgColor = textureLod(sceneColor, bgUV, 0.0).rgb;

    // Underwater colour: fog absorption.
    float fogFactor = exp2(-fogDensity * max(diff, 0.0));
    vec3 throughWater = mix(u.fogColor.rgb, bgColor, fogFactor);

    // Surface highlights — kept from the legacy shader.
    vec3 R = reflect(-V, waveN);
    float skyGrad = R.y * 0.5 + 0.5;
    vec3 reflectColor = mix(vec3(0.30, 0.40, 0.50), vec3(0.50, 0.65, 0.85), skyGrad);

    float refractiveFactor = pow(max(dot(V, waveN), 0.0), 0.5);
    vec3 waterBase = mix(reflectColor, throughWater, refractiveFactor);

    vec3 reflectedLight = reflect(-L, waveN);
    float spec = pow(max(dot(reflectedLight, V), 0.0), 64.0);
    vec3 specHighlight = vec3(1.0, 0.95, 0.85) * spec * 0.5;

    float NdotL = max(dot(N, L), 0.0);
    vec3 lit = waterBase * (0.4 + NdotL * 0.6) + specHighlight;

    FragColor = vec4(lit, 1.0);
}
#endif
