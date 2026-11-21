using BmwDecode.Core.Translation;

namespace BmwDecode.Core.Tests;

public class TranslatorTests
{
    private static readonly Translator T = Translator.FromTables(
        [new("kurzschluss", "short circuit"), new("nach", "to"), new("masse", "ground"), new("kühlmittel", "coolant"),
         new("pumpe", "pump"), new("temperatur", "temperature"), new("signal", "signal"), new("unplausibel", "implausible"),
         new("erfüllt", "met"), new("testbedingungen", "test conditions"), new("ansteuerung", "activation")],
        [new("Fehler momentan vorhanden und bereits gespeichert", "Fault currently present and stored")]);

    [Fact]
    public void Exact_phrases_win_and_ignore_umlaut_spelling()
    {
        Assert.Equal("Fault currently present and stored", T.Translate("Fehler momentan vorhanden und bereits gespeichert"));
        Assert.Equal("Test conditions met", T.Translate("Testbedingungen erfuellt"));
    }

    [Fact]
    public void Words_compounds_and_codes_are_handled()
    {
        Assert.Equal("Short circuit to ground", T.Translate("Kurzschluss nach Masse"));
        Assert.Equal("Coolant pump, activation", T.Translate("Kühlmittelpumpe, Ansteuerung"));
        Assert.Equal("Coolant temperature signal implausible (DME)", T.Translate("Kuehlmitteltemperatur Signal unplausibel (DME)"));
        Assert.Equal("PT-CAN timeout", T.Translate("PT-CAN timeout"));
        Assert.Equal("Xyzabc", T.Translate("Xyzabc"));
    }

    [Fact]
    public void Disabled_translator_passes_text_through()
    {
        Assert.Equal("Kurzschluss nach Masse", Translator.Passthrough.Translate("Kurzschluss nach Masse"));
    }

    [Fact]
    public void Built_in_tables_load()
    {
        var t = Translator.CreateDefault();
        Assert.True(t.WordCount > 5000);
        Assert.Equal("Short circuit to ground", t.Translate("Kurzschluss nach Masse"));
    }
}
