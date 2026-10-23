#ifndef FLUID_COMMON_INCLUDED
#define FLUID_COMMON_INCLUDED

#include "UnityCG.cginc"

// Shared full-screen-quad vertex shader + neighbor UVs, mirroring the
// baseVertexShader from the original WebGL fluid simulation:
// https://github.com/PavelDoGreat/WebGL-Fluid-Simulation

struct appdata_fluid
{
    float4 vertex : POSITION;
    float2 texcoord : TEXCOORD0;
};

struct v2f_fluid
{
    float4 pos : SV_POSITION;
    float2 vUv : TEXCOORD0;
    float2 vL : TEXCOORD1;
    float2 vR : TEXCOORD2;
    float2 vT : TEXCOORD3;
    float2 vB : TEXCOORD4;
};

float2 _TexelSize;

v2f_fluid vert_fluid (appdata_fluid v)
{
    v2f_fluid o;
    o.pos = UnityObjectToClipPos(v.vertex);
    o.vUv = v.texcoord;
    o.vL = v.texcoord - float2(_TexelSize.x, 0.0);
    o.vR = v.texcoord + float2(_TexelSize.x, 0.0);
    o.vT = v.texcoord + float2(0.0, _TexelSize.y);
    o.vB = v.texcoord - float2(0.0, _TexelSize.y);
    return o;
}

#endif
