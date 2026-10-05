namespace FamilyApp.Core.Calendar;
public sealed record FamilyEvent(Guid Id,string Title,DateTime Start,DateTime End,string? Member,string? Notes)
{
 public static FamilyEvent Create(string title,DateTime start,DateTime end,string? member=null,string? notes=null){Validate(start,end);return new(Guid.NewGuid(),Clean(title),start,end,Optional(member),Optional(notes));}
 public FamilyEvent Update(string title,DateTime start,DateTime end,string? member,string? notes){Validate(start,end);return this with{Title=Clean(title),Start=start,End=end,Member=Optional(member),Notes=Optional(notes)};}
 private static void Validate(DateTime start,DateTime end){if(end<=start)throw new ArgumentException("Event end must be after its start.");}
 private static string Clean(string value)=>string.IsNullOrWhiteSpace(value)?throw new ArgumentException("Event title is required.",nameof(value)):value.Trim();
 private static string? Optional(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
}
