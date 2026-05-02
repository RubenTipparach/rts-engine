// Atmospheric scattering — GLSL port of atmosphere.wgsl. Simplified Nishita
// single-scatter, alpha-blended on a transparent shell sphere larger than the
// planet.

layout(std140, binding = 0) uniform U {
    mat4 mvp;
    vec4 sunDir;
    vec4 cameraPos;
    vec4 params; // x = planetRadius, y = atmosphereRadius, z = sunIntensity
} u;

const float LOG_DEPTH_FAR = 10000.0;
vec4 applyLogDepth(vec4 p) {
    float logZ = log2(max(1e-6, 1.0 + p.w)) / log2(1.0 + LOG_DEPTH_FAR);
    return vec4(p.x, p.y, logZ * p.w, p.w);
}

#ifdef VERTEX
layout(location = 0) in vec3 aPos;
out vec3 vWorldPos;

void main() {
    gl_Position = applyLogDepth(u.mvp * vec4(aPos, 1.0));
    vWorldPos = aPos;
}
#endif

#ifdef FRAGMENT
in vec3 vWorldPos;
out vec4 FragColor;

const float PI = 3.14159265;
const int NUM_SAMPLES = 8;
const int NUM_LIGHT_SAMPLES = 4;
const float SCALE_HEIGHT = 0.5;
const vec3 WAVE_INV4 = vec3(5.602, 9.473, 19.644);

float rayleighPhase(float ct) { return 0.75 * (1.0 + ct * ct); }

float miePhase(float ct, float g) {
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(1.0 + g2 - 2.0 * g * ct, 1.5));
}

vec2 raySphere(vec3 ro, vec3 rd, float r) {
    float b = dot(ro, rd);
    float c = dot(ro, ro) - r * r;
    float d = b * b - c;
    if (d < 0.0) return vec2(-1.0);
    float sq = sqrt(d);
    return vec2(-b - sq, -b + sq);
}

float density(float alt) { return exp(-alt / SCALE_HEIGHT); }

float lightOpticalDepth(vec3 ro, vec3 rd, float len, float pR, float aR) {
    float step = len / float(NUM_LIGHT_SAMPLES);
    float d = 0.0;
    for (int i = 0; i < NUM_LIGHT_SAMPLES; i++) {
        vec3 p = ro + rd * (step * (float(i) + 0.5));
        float alt = clamp((length(p) - pR) / (aR - pR), 0.0, 1.0);
        d += density(alt) * step;
    }
    return d;
}

void main() {
    float pR = u.params.x;
    float aR = u.params.y;
    float sun = u.params.z;
    vec3 L = normalize(u.sunDir.xyz);
    vec3 ro = u.cameraPos.xyz;
    vec3 rd = normalize(vWorldPos - ro);

    vec2 aHit = raySphere(ro, rd, aR);
    if (aHit.y < 0.0) discard;

    float tStart = max(0.0, aHit.x);
    float tEnd = aHit.y;

    vec2 pHit = raySphere(ro, rd, pR);
    if (pHit.x > 0.0) tEnd = min(tEnd, pHit.x);
    if (tStart >= tEnd) discard;

    float step = (tEnd - tStart) / float(NUM_SAMPLES);
    float rScale = 0.005;
    float mScale = 0.003;
    float mG = 0.76;

    vec3 rSum = vec3(0.0);
    vec3 mSum = vec3(0.0);
    float odR = 0.0;
    float odM = 0.0;

    for (int i = 0; i < NUM_SAMPLES; i++) {
        vec3 sp = ro + rd * (tStart + step * (float(i) + 0.5));
        float h = length(sp);
        if (h < pR) continue;

        float alt = clamp((h - pR) / (aR - pR), 0.0, 1.0);
        float ld = density(alt);
        odR += ld * step;
        odM += ld * step;

        vec2 spHit = raySphere(sp, L, pR);
        if (spHit.x > 0.0) continue;

        vec2 saHit = raySphere(sp, L, aR);
        float sod = lightOpticalDepth(sp, L, max(0.0, saHit.y), pR, aR);

        vec3 tR = rScale * WAVE_INV4 * (odR + sod);
        vec3 tM = vec3(mScale * (odM + sod));
        vec3 att = exp(-(tR + tM));

        rSum += ld * att * step;
        mSum += ld * att * step;
    }

    rSum *= rScale * WAVE_INV4;
    mSum *= mScale;

    float ct = dot(rd, L);
    vec3 col = sun * (rSum * rayleighPhase(ct) + mSum * miePhase(ct, mG));
    col = 1.0 - exp(-col);

    float a = clamp(length(col) * 2.0, 0.0, 0.9);
    FragColor = vec4(col, a);
}
#endif
