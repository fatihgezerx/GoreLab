namespace GoreLab
{
    /// <summary>
    /// The parts of a humanoid body that can be cut off. A cut is made at the joint where the part starts and takes everything below it
    /// along (cutting an upper arm takes the forearm and the hand with it). A character that is not a humanoid names its own cuts and
    /// reaches them by name or number; <see cref="None"/> is what such a cut carries.
    /// </summary>
    /// <remarks>The values are saved in assets and in other systems' data: only ever append new ones at the end.</remarks>
    public enum GoreLimb : byte
    {
        None = 0,
        Head = 1,
        LeftUpperArm = 2,
        LeftLowerArm = 3,
        LeftHand = 4,
        RightUpperArm = 5,
        RightLowerArm = 6,
        RightHand = 7,
        LeftUpperLeg = 8,
        LeftLowerLeg = 9,
        LeftFoot = 10,
        RightUpperLeg = 11,
        RightLowerLeg = 12,
        RightFoot = 13
    }
}
