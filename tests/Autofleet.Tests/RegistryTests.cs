using System.Globalization;
using MiningDrone.Autofleet;
using Xunit;

namespace MiningDrone.Tests;

public class RegistryTests
{
    [Fact]
    public void NumberText_PercentInfiniteAndStep()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Assert.Equal("65%", Registry.NumberText(new ParamDef(Registry.PThreshold, ParamKind.Number, 0, 0, 1, 0.05), 0.65, "∞"));
        Assert.Equal("∞", Registry.NumberText(new ParamDef(Registry.PMaxSearch, ParamKind.Number, 0, 2000, Registry.UnlimitedSearch, 500), Registry.UnlimitedSearch, "∞"));
        Assert.Equal("30000", Registry.NumberText(new ParamDef(Registry.PMaxSearch, ParamKind.Number, 0, 2000, Registry.UnlimitedSearch, 500), 30000, "∞"));
        Assert.Equal("3000", Registry.NumberText(new ParamDef("r", ParamKind.Number, 0, 0, 5000, 50), 3000.4, "∞"));
        Assert.Equal("1.25", Registry.NumberText(new ParamDef("x", ParamKind.Number, 0, 0, 5, 0.05), 1.25, "∞"));
    }

    [Fact]
    public void Summary_DefaultsChoicesBoolsAndNoMutation()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        int checkedChoice = 0, checkedBool = 0;
        foreach (ModuleDef d in Registry.All)
        {
            var m = new ModuleInstance { Id = "x", Type = d.TypeId };
            string s = Registry.Summary(m, d, static k => k);
            Assert.Empty(m.Params); // inherited/default values read, never materialized
            foreach (ParamDef pd in d.Params)
            {
                if (pd.Kind == ParamKind.Choice)
                {
                    Assert.Contains(pd.LabelKey + ": " + pd.Choices![(int)pd.Default], s);
                    checkedChoice++;
                }
                else if (pd.Kind == ParamKind.Bool && pd.Inherit == null)
                {
                    m.Params[pd.Key] = 1;
                    Assert.Contains(pd.LabelKey, Registry.Summary(m, d, static k => k));
                    m.Params[pd.Key] = 0;
                    Assert.DoesNotContain(pd.LabelKey, Registry.Summary(m, d, static k => k));
                    m.Params.Clear();
                    checkedBool++;
                }
            }
        }
        Assert.True(checkedChoice > 0 && checkedBool > 0);
    }
}