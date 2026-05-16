using UnityEngine;

public class IngotData : MonoBehaviour
{
    [Tooltip("Display name shown in the tooltip, e.g. 'Gold', 'Copper', 'Steel'.")]
    public string materialName = "Unknown Material";

    [Tooltip("Thermal diffusivity in m²/s (scientific notation is fine in the Inspector).")]
    public float diffusivity = 1.0e-5f;

    //Returns a ready-to-display diffusivity string in scientific notation
    public string DiffusivityFormatted()
    {
        if (diffusivity == 0f) 
            return "0 m²/s";

        float abs = Mathf.Abs(diffusivity);
        int exp = Mathf.FloorToInt(Mathf.Log10(abs));
        float coeff = diffusivity / Mathf.Pow(10f, exp);

        return $"{coeff:F2}e{exp} m2/s";
    }
}