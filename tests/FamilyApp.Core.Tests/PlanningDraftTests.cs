using System.Globalization;
using FamilyApp.Core.Meals;
using FamilyApp.Core.Planning;
using Xunit;

namespace FamilyApp.Core.Tests;
public sealed class PlanningDraftTests
{
    private static string Json(string extra = "") => """
        {"version":1,"importId":"f0ee49a6-1a36-4e88-af9b-8e05d8dbcc71","weekStart":"2026-10-12",
        "meals":[{"date":"2026-10-13","slot":"Dinner","dish":"Tortilla española","agreed":true},
        {"date":null,"slot":null,"dish":"Quizá pasta","agreed":false}],
        "shopping":[{"name":"Huevos","quantity":"6","unit":null,"source":"explicit"},
        {"name":"Patatas","quantity":null,"unit":null,"source":"inferred"}],"questions":[]}
        """ + extra;
    [Fact]
    public void SpanishAgreementsAndExplicitPurchasesAreSelectedButSuggestionsAreNot()
    {
        var service = new PlanningImportService(new NeverSave());
        var draft = service.Read(Json());
        var prepared = draft.Prepare();
        Assert.Equal("Tortilla española", Assert.Single(prepared.Meals).Description);
        Assert.Equal("6", Assert.Single(prepared.Shopping).Quantity);
        Assert.False(draft.Meals[1].Include); Assert.False(draft.Shopping[1].Include);
        Assert.Equal(prepared.Meals[0].Id, service.Read(Json()).Prepare().Meals[0].Id);
    }
    [Fact]
    public void ImportedEntityIdsAreIdenticalAcrossDeviceCultures()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var english = new PlanningImportService(new NeverSave()).Read(Json()).Prepare();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var arabic = new PlanningImportService(new NeverSave()).Read(Json()).Prepare();
            Assert.Equal(english.Meals.Select(x => x.Id), arabic.Meals.Select(x => x.Id));
            Assert.Equal(english.Shopping.Select(x => x.Id), arabic.Shopping.Select(x => x.Id));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
    [Theory]
    [InlineData("missing-date")]
    [InlineData("out-of-week")]
    [InlineData("question")]
    [InlineData("duplicate-slot")]
    public void UnresolvedOrConflictingDraftCannotBeSaved(string scenario)
    {
        var draft = new PlanningImportService(new NeverSave()).Read(Json());
        switch (scenario)
        {
            case "missing-date": draft.Meals[0].Date = null; break;
            case "out-of-week": draft.Meals[0].Date = new(2026, 11, 1); break;
            case "question": draft.Questions.Add("¿Qué día?"); break;
            default: draft.Meals.Add(new() { Date = draft.Meals[0].Date, Slot = MealType.Dinner, Dish = "Pasta", Include = true }); break;
        }
        Assert.Throws<ArgumentException>(() => draft.Prepare());
    }
    [Fact]
    public void RejectsInvalidJsonUnknownFieldsAndMissingIdentity()
    {
        Assert.Throws<ArgumentException>(() => PlanningDraft.Parse(Json(" trailing")));
        Assert.Throws<ArgumentException>(() => PlanningDraft.Parse(Json().Replace("\"version\":1", "\"version\":99")));
        Assert.Throws<ArgumentException>(() => PlanningDraft.Parse(Json().Replace("\"version\":1", "\"unknown\":1")));
        Assert.Throws<ArgumentException>(() => PlanningDraft.Parse(Json().Replace("f0ee49a6-1a36-4e88-af9b-8e05d8dbcc71", Guid.Empty.ToString())));
        Assert.Throws<ArgumentException>(() => PlanningDraft.Parse(Json().Replace(",\"source\":\"explicit\"", "")));
        Assert.Equal(1, PlanningDraft.Parse("```json\n" + Json() + "\n```").Version);
    }
    private sealed class NeverSave : IPlanningImportStore
    {
        public Task<PlanningImportResult> SaveAsync(PreparedPlanningImport draft) => throw new InvalidOperationException();
    }
}
