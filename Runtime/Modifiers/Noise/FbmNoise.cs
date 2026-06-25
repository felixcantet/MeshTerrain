using Unity.Mathematics;

namespace Fca.MeshTerrain
{
    /// <summary>FBM accumulation mode. Unity port of UE <c>Geometry::EFBMMode</c>.</summary>
    public enum FbmMode
    {
        /// <summary>Signed noise summed across octaves (default).</summary>
        Standard,
        /// <summary>Absolute value per octave — billowy / turbulent.</summary>
        Turbulent,
        /// <summary>Inverted absolute value per octave — sharp ridges.</summary>
        Ridge,
    }

    /// <summary>
    /// Fractal Brownian Motion noise. Unity port of UE <c>Geometry::FractalBrownianMotionNoise</c> (used by
    /// <c>MeshPartitionNoiseModifier.cpp</c>). The UE original draws on GeometryCore's noise basis; per
    /// <c>doc/02_SYSTEM_ANALYSIS.md §5.2</c> the sanctioned substitution is a standard noise lib — here
    /// <see cref="Unity.Mathematics.noise.snoise"/> (simplex). The octave accumulation, the three modes,
    /// and the lacunarity/gain/smoothness/gamma shaping mirror the UE math.
    /// </summary>
    public static class FbmNoise
    {
        /// <summary>
        /// Evaluates FBM at <paramref name="st"/>.
        /// </summary>
        /// <param name="mode">Accumulation mode.</param>
        /// <param name="octaves">Number of octaves summed.</param>
        /// <param name="st">Sample coordinate (already translated/rotated/scaled by the caller).</param>
        /// <param name="lacunarity">Frequency multiplier between octaves (UE default 2).</param>
        /// <param name="gain">Amplitude multiplier between octaves (UE default 0.5).</param>
        /// <param name="smoothness">Exponent shaping each octave's contribution (UE default 1).</param>
        /// <param name="gamma">Exponent applied to the normalised result (UE default 1).</param>
        public static double Evaluate(FbmMode mode, int octaves, double2 st,
            double lacunarity, double gain, double smoothness, double gamma)
        {
            double sum = 0.0;
            double amplitude = 1.0;
            double frequency = 1.0;
            double maxAmplitude = 0.0;

            for (int o = 0; o < octaves; o++)
            {
                // snoise is in [-1,1]; sample at the octave frequency.
                double n = noise.snoise((float2)(st * frequency));

                switch (mode)
                {
                    case FbmMode.Turbulent:
                        n = math.abs(n);
                        break;
                    case FbmMode.Ridge:
                        n = 1.0 - math.abs(n);
                        n *= n; // sharpen ridges (matches UE ridged shaping)
                        break;
                    // Standard: signed n as-is.
                }

                // Smoothness shapes each octave's contribution (1 = linear).
                if (smoothness != 1.0)
                    n = math.sign(n) * math.pow(math.abs(n), smoothness);

                sum += n * amplitude;
                maxAmplitude += amplitude;

                amplitude *= gain;
                frequency *= lacunarity;
            }

            // Normalise to keep the result roughly in the base noise range regardless of octave count.
            double result = maxAmplitude > 0.0 ? sum / maxAmplitude : 0.0;

            // Gamma shaping of the final value (1 = no-op).
            if (gamma != 1.0)
                result = math.sign(result) * math.pow(math.abs(result), gamma);

            return result;
        }
    }
}
