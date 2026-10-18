using RobotGateway.Core;

namespace RobotGateway.Tests;

public class ProgramCatalogTests
{
    [Fact]
    public void InvalidEntries_AreExcluded_WithErrors()
    {
        var catalog = new ProgramCatalog(
        [
            new WorkProgram { Id = "ok", Name = "Ok", PlanName = "P1" },
            new WorkProgram { Id = "ok", Name = "Duplicate", PlanName = "P2" },
            new WorkProgram { Id = "no-plan", Name = "No plan" },
            new WorkProgram { Id = "too-fast", Name = "Too fast", PlanName = "P3", VelocityScale = 90, MaxVelocityScale = 80 },
            new WorkProgram { Id = "bad id!", Name = "Bad id", PlanName = "P4" },
            new WorkProgram { Id = "prim", Name = "Primitives", Kind = ProgramKind.PrimitiveSequence },
        ]);

        Assert.Single(catalog.Programs);
        Assert.Equal(5, catalog.Errors.Count);
        Assert.NotNull(catalog.FindByPlanName("P1"));
        Assert.Null(catalog.FindByPlanName("P3"));
    }

    [Fact]
    public void LoadJson_ReadsVersionedFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {
                  // comment allowed
                  "schemaVersion": 1,
                  "programs": [
                    { "id": "home", "name": "Home", "kind": "ElementsPlan", "planName": "PLAN-Home", "velocityScale": 20, "maxVelocityScale": 50 }
                  ]
                }
                """);

            var catalog = ProgramCatalog.LoadJson(path);

            Assert.Empty(catalog.Errors);
            var program = Assert.Single(catalog.Programs);
            Assert.Equal(50, program.MaxVelocityScale);
            Assert.Same(program, catalog.FindById("HOME"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadJson_MissingOrWrongVersion_GivesEmptyCatalogWithError()
    {
        Assert.NotEmpty(ProgramCatalog.LoadJson(Path.Combine(Path.GetTempPath(), "does-not-exist.json")).Errors);

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{ "schemaVersion": 99, "programs": [] }""");
            var catalog = ProgramCatalog.LoadJson(path);
            Assert.Empty(catalog.Programs);
            Assert.NotEmpty(catalog.Errors);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
