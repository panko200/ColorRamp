cbuffer Constants : register(b0)
{
    int mixMode; // 0:RGB, 1:HSL, 2:HSV
    int keepCh1;
    int keepCh2;
    int keepCh3;
    int keepAlpha;
    float3 padding;
};

// Input 0: Gradient Mapped Texture
Texture2D<float4> InputTexture : register(t0);
SamplerState InputSampler : register(s0);

// Input 1: Original Texture
Texture2D<float4> OriginalTexture : register(t1);
SamplerState OriginalSampler : register(s1);

// --- RGB <-> HSL/HSV Helper Functions ---

float3 RGBToHSL(float3 c)
{
    float maxV = max(max(c.r, c.g), c.b);
    float minV = min(min(c.r, c.g), c.b);
    float h, s, l = (maxV + minV) / 2.0;

    if (maxV == minV)
    {
        h = s = 0.0;
    }
    else
    {
        float d = maxV - minV;
        s = l > 0.5 ? d / (2.0 - maxV - minV) : d / (maxV + minV);
        if (maxV == c.r)
            h = (c.g - c.b) / d + (c.g < c.b ? 6.0 : 0.0);
        else if (maxV == c.g)
            h = (c.b - c.r) / d + 2.0;
        else
            h = (c.r - c.g) / d + 4.0;
        h /= 6.0;
    }
    return float3(h, s, l);
}

float HueToRGB(float p, float q, float t)
{
    if (t < 0.0)
        t += 1.0;
    if (t > 1.0)
        t -= 1.0;
    if (t < 1.0 / 6.0)
        return p + (q - p) * 6.0 * t;
    if (t < 1.0 / 2.0)
        return q;
    if (t < 2.0 / 3.0)
        return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
    return p;
}

float3 HSLToRGB(float3 hsl)
{
    float h = hsl.x;
    float s = hsl.y;
    float l = hsl.z;
    if (s == 0.0)
        return float3(l, l, l);
    float q = l < 0.5 ? l * (1.0 + s) : l + s - l * s;
    float p = 2.0 * l - q;
    return float3(HueToRGB(p, q, h + 1.0 / 3.0), HueToRGB(p, q, h), HueToRGB(p, q, h - 1.0 / 3.0));
}

float3 RGBToHSV(float3 c)
{
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = c.g < c.b ? float4(c.bg, K.wz) : float4(c.gb, K.xy);
    float4 q = c.r < p.x ? float4(p.xyw, c.r) : float4(c.r, p.yzx);
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

float3 HSVToRGB(float3 c)
{
    float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * lerp(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

// ----------------------------------------

float4 main(float4 pos : SV_POSITION, float4 posScene : SCENE_POSITION, float4 uv0 : TEXCOORD0) : SV_Target
{
    float4 gradColor = InputTexture.Sample(InputSampler, uv0.xy);
    float4 origColor = OriginalTexture.Sample(OriginalSampler, uv0.xy);

    // Unpremultiply Alpha (Straight RGBに戻して計算)
    float3 c1 = gradColor.rgb;
    if (gradColor.a > 0.0)
        c1 /= gradColor.a;

    float3 c2 = origColor.rgb;
    if (origColor.a > 0.0)
        c2 /= origColor.a;

    float3 result = c1;

    // --- RGB Mode ---
    if (mixMode == 0)
    {
        result.r = (keepCh1 ? c2.r : c1.r);
        result.g = (keepCh2 ? c2.g : c1.g);
        result.b = (keepCh3 ? c2.b : c1.b);
    }
    // --- HSL Mode ---
    else if (mixMode == 1)
    {
        float3 h1 = RGBToHSL(c1);
        float3 h2 = RGBToHSL(c2);
        
        float3 hFinal;
        hFinal.x = (keepCh1 ? h2.x : h1.x); // H
        hFinal.y = (keepCh2 ? h2.y : h1.y); // S
        hFinal.z = (keepCh3 ? h2.z : h1.z); // L
        
        result = HSLToRGB(hFinal);
    }
    // --- HSV Mode ---
    else if (mixMode == 2)
    {
        float3 h1 = RGBToHSV(c1);
        float3 h2 = RGBToHSV(c2);
        
        float3 hFinal;
        hFinal.x = (keepCh1 ? h2.x : h1.x); // H
        hFinal.y = (keepCh2 ? h2.y : h1.y); // S
        hFinal.z = (keepCh3 ? h2.z : h1.z); // V
        
        result = HSVToRGB(hFinal);
    }

    // Alpha Handling
    float finalAlpha = (keepAlpha ? origColor.a : gradColor.a);

    // Premultiply Alpha (描画用にα乗算済みに戻す)
    return float4(result * finalAlpha, finalAlpha);
}