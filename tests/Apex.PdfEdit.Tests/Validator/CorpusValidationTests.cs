using Apex.PdfEdit.Core.Io;
using Apex.PdfEdit.Core.Validator;
using FluentAssertions;
using Xunit;

namespace Apex.PdfEdit.Tests.Validator;

/// <summary>
/// Cross-checks the .NET validator's output against the Python <c>_validator_baseline.py</c>
/// numbers under Rev 1.2. Expected counts are hard-coded from
/// <c>prep/validator_baseline_output_rev12.txt</c>.
/// </summary>
public sealed class CorpusValidationTests
{
    private static readonly IReadOnlyDictionary<string, long> Expected = new Dictionary<string, long>
    {
        ["05-15-2025 Board Packet-Remediated"] = 56L,
        ["1TCC-MS4-Staff-Handbook-2024-03-14-Remediated"] = 59L,
        ["2026 Proxy 2.24.26_WEB_ADA"] = 26L,
        ["452032_1_1_Bessemer Trust_July 2025_Portfolio_Summaries_MF_Only_ADA"] = 10L,
        ["949163_Guided Notes_PLATO Course Introduction to Visual Arts_ Principles of Design"] = 6L,
        ["CARE Application_Espanol-Remediated"] = 0L,
        ["EA Application_English-Remediated"] = 1L,
        ["ImplementationGuidelines-l241_Accessible"] = 13L,
        ["UDO 26-2652 HGP Estate Planning Kit_F2-Remediated"] = 27L,
        ["form-40x-2016-Remediated"] = 0L
    };

    [FactIfSample("")]
    public void DotNetValidatorAgreesWithPythonBaselineAcrossCorpus()
    {
        var schema = SchemaLoader.LoadDefault();
        var validator = new TagTreeValidator(schema);

        var actual = new Dictionary<string, long>();
        foreach (var e in Expected)
        {
            var json = TestSamples.Resolve(e.Key, e.Key + "-document.json");
            if (!File.Exists(json))
            {
                // Skip missing samples — corpus may be pared down locally.
                continue;
            }
            var doc = DocumentJsonLoader.Load(json);
            long got = validator.Validate(doc).DistinctErrorNodes();
            actual[e.Key] = got;
        }

        actual.Should().NotBeEmpty();
        foreach (var e in actual)
        {
            e.Value.Should().Be(Expected[e.Key], $"distinct error nodes for {e.Key}");
        }
    }
}
