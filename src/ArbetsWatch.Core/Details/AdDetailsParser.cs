using System.Globalization;
using System.Text.Json;
using ArbetsWatch.Core.Presentation;
using ArbetsWatch.Core.Time;

namespace ArbetsWatch.Core.Details;

/// <summary>Projects a full ad (JobSearch <c>/ad/{id}</c>; the same shape as JobStream ads) into <see cref="AdDetails"/>.</summary>
public static class AdDetailsParser
{
    private static readonly (string Key, string Category)[] RequirementKinds =
    [
        ("work_experiences", "Experience"),
        ("skills", "Skill"),
        ("languages", "Language"),
        ("education", "Education"),
        ("education_level", "Education level"),
    ];

    public static AdDetails Parse(JsonElement ad)
    {
        var id = Str(ad, "id") ?? throw new JsonException("The ad has no id.");
        var employer = Obj(ad, "employer");
        var address = Obj(ad, "workplace_address");
        var application = Obj(ad, "application_details");

        var requirements = new List<AdRequirement>();
        foreach (var (key, category) in RequirementKinds)
        {
            requirements.AddRange(Labels(Obj(ad, "must_have"), key).Select(l => new AdRequirement(category, l, Required: true)));
            requirements.AddRange(Labels(Obj(ad, "nice_to_have"), key).Select(l => new AdRequirement(category, l, Required: false)));
        }

        var contacts = new List<AdContact>();
        if (ad.TryGetProperty("application_contacts", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in list.EnumerateArray())
            {
                var contact = new AdContact(Str(c, "name"), Str(c, "description") ?? Str(c, "contact_type"), ExternalLinks.Email(Str(c, "email")), Str(c, "telephone"));
                if (contact.Name is not null || contact.Email is not null || contact.Phone is not null)
                {
                    contacts.Add(contact);
                }
            }
        }

        var platsbanken = AdLinkPolicy.Resolve(id, Str(ad, "webpage_url"));
        return new AdDetails
        {
            Id = id,
            Headline = Str(ad, "headline") ?? "Untitled ad",
            Employer = Str(employer, "name"),
            Workplace = Str(employer, "workplace"),
            EmployerWebsite = ExternalLinks.WebLink(Str(employer, "url")),
            Description = Str(Obj(ad, "description"), "text"),
            Occupation = Label(ad, "occupation"),
            EmploymentType = Label(ad, "employment_type"),
            Duration = Label(ad, "duration"),
            WorkingHours = Label(ad, "working_hours_type"),
            Scope = Scope(Obj(ad, "scope_of_work")),
            Vacancies = Int(ad, "number_of_vacancies"),
            SalaryType = Label(ad, "salary_type"),
            SalaryDescription = Str(ad, "salary_description"),
            StreetAddress = Str(address, "street_address"),
            Postcode = Str(address, "postcode"),
            City = Str(address, "city"),
            Municipality = Str(address, "municipality"),
            Region = Str(address, "region"),
            Country = Str(address, "country"),
            Published = SwedishTime.ParseLocal(Str(ad, "publication_date")),
            LastPublication = SwedishTime.ParseLocal(Str(ad, "last_publication_date")),
            ApplicationDeadline = SwedishTime.ParseLocal(Str(ad, "application_deadline")),
            ExperienceRequired = Bool(ad, "experience_required"),
            DrivingLicenseRequired = Bool(ad, "driving_license_required"),
            DrivingLicenses = ad.TryGetProperty("driving_license", out var licenses) && licenses.ValueKind == JsonValueKind.Array
                ? [.. licenses.EnumerateArray().Select(l => Str(l, "label")).OfType<string>()]
                : [],
            OwnCarRequired = Bool(ad, "access_to_own_car"),
            Requirements = requirements,
            Contacts = contacts,
            Application = new AdApplication(
                ExternalLinks.WebLink(Str(application, "url")),
                ExternalLinks.Email(Str(application, "email")),
                Str(application, "reference"),
                Str(application, "information"),
                Str(application, "other"),
                Bool(application, "via_af") == true),
            PlatsbankenPage = platsbanken,
        };
    }

    private static string? Scope(JsonElement scope)
    {
        var min = Int(scope, "min");
        var max = Int(scope, "max");
        return (min, max) switch
        {
            (null, null) => null,
            ({ } a, { } b) when a != b => string.Create(CultureInfo.InvariantCulture, $"{a}–{b} %"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{min ?? max} %"),
        };
    }

    private static IEnumerable<string> Labels(JsonElement group, string key) =>
        group.ValueKind == JsonValueKind.Object && group.TryGetProperty(key, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(i => Str(i, "label")).OfType<string>()
            : [];

    private static string? Label(JsonElement ad, string name) => Str(Obj(ad, name), "label");

    private static JsonElement Obj(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? Str(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;
}
