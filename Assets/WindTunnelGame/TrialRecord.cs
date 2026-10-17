using System.Collections.Generic;

[System.Serializable]
public class TrialRecord
{
    public string id;           // unique, for the database later
    public int number;
    public string name;
    public string savedAt;      // ISO date/time
    public string note;         // "Saved", "Fired", ...
    public NoseShape shape;

    // Shape numbers at save time
    public float noseAngle, bluntness, fineness, fullness, tipBend;
    public float predictedLow, predictedHigh;    // predicted smooth flow at Mach 1.6 / 2.2

    // Filled in later by the wind tunnel
    public bool hasResult;
    public float mach, smoothFlow;

    // Thumbnail cache: rebuilt when needed, not saved
    [System.NonSerialized] public float[] thumbTop, thumbBottom;
}

[System.Serializable]
public class TrialFile
{
    public List<TrialRecord> trials = new();
}