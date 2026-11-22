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

public class JobNameTests
{
    private static Translation.Translator Tr() => Translation.Translator.FromTables(
        [new("raildruck", "rail pressure"), new("lüfter", "fan"), new("prüfstempel", "test stamp"), new("motordrehzahl", "engine speed")], []);

    [Fact]
    public void JobNamesReadAsEnglish()
    {
        var t = Tr();
        Assert.Equal("Status rail pressure actual", t.TranslateIdentifier("STATUS_RAILDRUCK_IST"));
        Assert.Equal("Control electric fan", t.TranslateIdentifier("STEUERN_E_LUEFTER"));
        Assert.Equal("Fault memory read", t.TranslateIdentifier("FS_LESEN"));
        Assert.Equal("Coding read", t.TranslateIdentifier("C_C_LESEN"));
        Assert.Equal("Test stamp read", t.TranslateIdentifier("PRUEFSTEMPEL_LESEN"));
        Assert.Equal("Status DME 2", t.TranslateIdentifier("STATUS_DME_2"));
    }

    [Fact]
    public void ResultNamesDropStatAndWert()
    {
        var previous = Translation.Translator.Current;
        try
        {
            Translation.Translator.Current = Tr();
            Assert.Equal("Engine speed", Translation.JobNames.Result(null, "STATUS_MOTORDREHZAHL", "STAT_MOTORDREHZAHL_WERT"));
        }
        finally
        {
            Translation.Translator.Current = previous;
        }
    }

    [Fact]
    public void DisabledTranslatorKeepsNames()
    {
        var t = Tr();
        t.Enabled = false;
        Assert.Equal("STATUS_RAILDRUCK_IST", t.TranslateIdentifier("STATUS_RAILDRUCK_IST"));
    }
}
