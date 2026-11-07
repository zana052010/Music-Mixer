using MusicMixer.Domain.Alignment;
using MusicMixer.Domain.Models;

namespace MusicMixer.Tests;

public sealed class MusicalKeyTests
{
    [Theory]
    [InlineData(PitchClass.C, KeyMode.Major, "8B")]
    [InlineData(PitchClass.A, KeyMode.Minor, "8A")]
    [InlineData(PitchClass.FSharp, KeyMode.Major, "2B")]
    [InlineData(PitchClass.B, KeyMode.Minor, "10A")]
    [InlineData(PitchClass.DSharp, KeyMode.Minor, "2A")]
    public void CamelotNotation_IsCorrect(PitchClass tonic, KeyMode mode, string expected)
    {
        Assert.Equal(expected, new MusicalKey(tonic, mode).Camelot);
    }

    [Theory]
    [InlineData(PitchClass.B, KeyMode.Minor, PitchClass.C, KeyMode.Major, 1)]
    [InlineData(PitchClass.A, KeyMode.Minor, PitchClass.C, KeyMode.Major, 0)]
    [InlineData(PitchClass.C, KeyMode.Major, PitchClass.A, KeyMode.Minor, 0)]
    [InlineData(PitchClass.D, KeyMode.Minor, PitchClass.C, KeyMode.Major, -2)]
    [InlineData(PitchClass.C, KeyMode.Major, PitchClass.FSharp, KeyMode.Major, -6)]
    [InlineData(PitchClass.C, KeyMode.Major, PitchClass.C, KeyMode.Minor, 0)]
    public void KeyTransposition_ReturnsShortestMusicallyUsefulShift(
        PitchClass sourceTonic,
        KeyMode sourceMode,
        PitchClass targetTonic,
        KeyMode targetMode,
        int expectedSemitones)
    {
        var shift = KeyTransposition.CalculateSemitones(
            new MusicalKey(sourceTonic, sourceMode),
            new MusicalKey(targetTonic, targetMode));

        Assert.Equal(expectedSemitones, shift);
    }
}
