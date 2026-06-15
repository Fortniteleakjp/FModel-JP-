using System;

namespace CUE4Parse_Conversion.Options;

// Back-ported from CUE4Parse PR #358 (new export pipeline / landscape DTO).
[Flags]
public enum ELandscapeFlags
{
    Heightmap = 1 << 0,
    Weightmap = 1 << 1,
    Mesh = 1 << 2,
    All = Heightmap | Weightmap | Mesh
}
