namespace DreamBooks.Pdf.Tests;

/// <summary>
/// The tests that time and weigh a whole save on a long real document run alone: xUnit runs this collection after
/// every other one has finished. Time and private bytes are measured for the whole process, so any test running beside
/// them (on the 8 GB dev Surface) would be counted in their numbers.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LargeDocumentCollection
{
    public const string Name = "Large document, run alone";
}
