// Standalone water shader (GLSL port of water.wgsl). See the WGSL file
// for the full design notes.

layout(std140, binding = 0) uniform U {
    mat4 mvp;
    vec4 sunDir;
    vec4 cameraPos;
    // params.x = time
    // params.z = water column thickness in world units
    vec4 params;
} u;

layout(binding = 2) uniform sampler2D waterDuDv;
layout(binding = 3) uniform sampler2D waterNormal;

const float LOG_DEPTH_FAR = 10000.0;
vec4 applyLogDepth(vec4 p) {
    float logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4(p.x, p.y, logZ * p.w, p.w);
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
    float oceanDepth = u.params.z;

    vec3 b = max(abs(N), vec3(0.001));
    float total = b.x + b.y + b.z;
    float wx = b.x / total;
    float wy = b.y / total;
    float wz = b.z / total;
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

    vec3 nmSample = textureLod(waterNormal, dudvUV2, 0.0).rgb;
    vec3 mapNormal = vec3(nmSample.r * 2.0 - 1.0, nmSample.b * 3.0, nmSample.g * 2.0 - 1.0);

    vec3 tang = cross(N, vec3(0.0, 1.0, 0.0));
    if (dot(tang, tang) < 0.01) tang = cross(N, vec3(1.0, 0.0, 0.0));
    tang = normalize(tang);
    vec3 bitang = normalize(cross(N, tang));
    vec3 waveN = normalize(tang * mapNormal.x + N * mapNormal.y + bitang * mapNormal.z);

    float viewCos = max(dot(N, V), 0.08);
    float pathLen = oceanDepth / viewCos;

    vec3 shallowColor = vec3(0.18, 0.55, 0.65);
    vec3 deepColor    = vec3(0.02, 0.10, 0.22);
    float depth01     = smoothstep(0.0, oceanDepth * 4.0, pathLen);
    vec3 throughWater = mix(shallowColor, deepColor, depth01);

    float depthFoamMask = 1.0 - smoothstep(0.0, oceanDepth * 1.5, pathLen);
    float foamPattern = textureLod(waterDuDv, dudvUV2 * 0.5, 0.0).g;
    float foamShape = smoothstep(0.35, 0.65, foamPattern + depthFoamMask * 0.5);
    float foam = foamShape * smoothstep(0.0, 1.0, depthFoamMask);

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
    vec3 withFoam = mix(lit, vec3(0.95, 0.97, 1.0), foam);

    float alphaCore = mix(0.55, 0.95, depth01);
    float alpha = max(alphaCore, foam);

    FragColor = vec4(withFoam, alpha);
}
#endif
