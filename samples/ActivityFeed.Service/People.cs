using ActivityFeed.Contracts;

namespace ActivityFeed.Service;

/// <summary>A site of the practice: its id (a tenancy, to the policy) and its name.</summary>
public sealed record Site(int Id, string Name)
{
    /// <summary>
    ///     The site's partition of the service (<c>north</c>: the service's path <c>practice/north</c>): routes of someone
    ///     who works or is registered here are held in it.
    /// </summary>
    public string Partition => Name.ToLowerInvariant();
}

public enum Role
{
    Reception,
    Clinician,
    Manager,
    Patient,
    Screen,
    Carer,
    Auditor
}

/// <summary>
///     A person of the practice, synthetic: staff, a patient, or a waiting-room screen (a device standing at one site).
///     A patient's year of birth, sex and registered GP; a clinician's room.
/// </summary>
public sealed record Person(int Id, string Name, Role Role, int[] Sites, string About)
{
    public short Born { get; init; }
    public char Sex { get; init; }
    public int Gp { get; init; }
    public string Room { get; init; } = "";

    /// <summary>A patient's own tags: each a name for a group of their problems (by title), to share by.</summary>
    public IReadOnlyDictionary<string, string[]> Tags { get; init; } = new Dictionary<string, string[]>();

    /// <summary>A patient who has opted out of the practice's informational notices (its news): one in ten.</summary>
    public bool OptedOutOfNews => Role == Role.Patient && Id % 10 == 7;

    /// <summary>A carer's designations: whom they care for, and in what scope (<c>admin</c>, <c>clinical</c>, <c>sensitive</c>).</summary>
    public IReadOnlyDictionary<int, string> Cares { get; init; } = new Dictionary<int, string>();

    /// <summary>Who they are to the policy (<c>@ctx.user</c>): a patient owns the rows whose <c>patient_user</c> is this.</summary>
    public string User => Role switch { Role.Patient => $"patient:{Id}", Role.Carer => $"carer:{Id}", _ => $"staff:{Id}" };

    public Site? Site => Sites.Length == 1 ? People.SiteOf(Sites[0]) : null;
}

/// <summary>
///     The practice: two sites, their staff, their patients and their screens. All synthetic: the patients are made from
///     lists of names with a fixed seed, so every run has the same ones.
/// </summary>
public static class People
{
    public static readonly Site North = new(1, "North"), South = new(2, "South");
    public static readonly Site[] Sites = [North, South];

    public static readonly Person[] Staff =
    [
        new(101, "Rosa Diaz", Role.Reception, [1],
            "Reception, North: every visit at North and who it's for; what for reads \"Consultation\"."),
        new(104, "Priya Shah", Role.Reception, [1],
            "Reception, North: Rosa's class, so the same view feeds her own topic."),
        new(102, "Sam Lewis", Role.Reception, [2], "Reception, South: South's visits only."),
        new(201, "Dr Anil Patel", Role.Clinician, [1],
                "GP, North: North's visits; what for, in full, where he attended or the patient shares it with him " +
                "(his own patients share everything); \"Not shared with you\" elsewhere.")
            { Room = "Room 4" },
        new(202, "Dr Chidi Okafor", Role.Clinician, [1],
                "GP, North: as Dr Patel, for his own patients and the visits he attended.")
            { Room = "Room 2" },
        new(203, "Dr Mei Lee", Role.Clinician, [2], "GP, South: South's visits; what for, for her patients.")
            { Room = "Room 1" },
        new(301, "Max Weber", Role.Manager, [1, 2],
            "Practice manager: both sites' appointments, who's booked and with whom, need to know; and their figures, counts and means only. Never why anyone came.")
    ];

    public static readonly Person[] Screens =
    [
        new(901, "Waiting room, North", Role.Screen, [1], "Waiting-room screen, North: tickets and rooms, nothing personal."),
        new(902, "Waiting room, South", Role.Screen, [2], "Waiting-room screen, South: tickets and rooms, nothing personal.")
    ];

    public static readonly Person[] Patients = MakePatients();

    /// <summary>The practice's privacy officer: the access log is theirs (the audit page), and nothing else of anyone's.</summary>
    public static readonly Person[] Auditors =
    [
        new(401, "Nadia Bauer", Role.Auditor, [1, 2],
            "Privacy officer: who was shown which record, from the access log, as the auditor role reads it. No feed of her own.")
    ];

    /// <summary>
    ///     Carers: people with a login of their own, designated by a patient (or for one by the practice), in a scope.
    /// </summary>
    public static readonly Person[] Carers =
    [
        new(3001, "Tom Smith", Role.Carer, [1],
                "Carer: Jane Smith's son, in the clinical scope: her visits and released results, never a sensitive one.")
            { Cares = new Dictionary<int, string> { [1001] = "clinical" } }
    ];

    /// <summary>
    ///     The devices their app registered, synthetic: most patients one (an iPhone or an Android phone), some none, Jane
    ///     Smith an iPhone and the web, her carer an Android phone, and Ann Lowe an old iPad whose token the last mile no
    ///     longer takes, beside her iPhone. Each with its push token.
    /// </summary>
    public static readonly Device[] Devices = MakeDevices();

    private static readonly Dictionary<int, Person> ById =
        Staff.Concat(Screens).Concat(Patients).Concat(Carers).Concat(Auditors).ToDictionary(p => p.Id);

    /// <summary>The persona picker's people: staff, the screens, a few patients (Jane Smith among them), and the auditor.</summary>
    public static Persona[] Personas =>
    [
        .. Staff.Concat(Screens).Concat(Patients.Where(p => p.Id is 1001 or 1002 or 2001)).Concat(Carers).Concat(Auditors)
            .Select(p => new Persona
        {
            Id = p.Id, Name = p.Name, Role = p.Role.ToString().ToLowerInvariant(),
            Site = p.Site?.Name, About = p.About, Room = p.Room is { Length: > 0 } room ? room : null,
            Tags = p.Tags.Count > 0 ? [.. p.Tags.Keys] : null
        })
    ];

    public static Person? Find(int id)
    {
        return ById.GetValueOrDefault(id);
    }

    public static Site SiteOf(int id)
    {
        return id == North.Id ? North : South;
    }

    /// <summary>A clinician's patients: those registered with them (their care list, to the policy).</summary>
    public static IEnumerable<Person> CareOf(Person clinician)
    {
        return Patients.Where(p => p.Gp == clinician.Id);
    }

    private static Person[] MakePatients()
    {
        string[] women =
            ["Ann", "Beth", "Chloe", "Dana", "Ella", "Fiona", "Grace", "Hana", "Isla", "Joy", "Kate", "Lena", "Maya", "Nora"];
        string[] men =
            ["Adam", "Ben", "Carl", "David", "Ethan", "Femi", "Gary", "Hugo", "Ivan", "Jack", "Kofi", "Luis", "Marc", "Noah"];
        string[] surnames =
        [
            "Lowe", "Haddad", "Brown", "Chen", "Evans", "Fischer", "Green", "Hughes", "Ito", "Jones", "Khan", "Lopez",
            "Murphy", "Nowak", "Owen", "Price", "Quinn", "Reid", "Silva", "Taylor", "Usman", "Vance", "Walsh", "Young"
        ];
        var random = new Random(20261015);
        var patients = new List<Person>
        {
            new(1001, "Jane Smith", Role.Patient, [1],
                    "Patient, North (Dr Patel; Dr Okafor too, for her diabetes): her own visits, in full; whom she shares what with.")
                {
                    Born = 1971, Sex = 'F', Gp = 201,
                    Tags = new Dictionary<string, string[]>
                        { ["My diabetes"] = ["Type 2 diabetes"], ["My blood pressure"] = ["Hypertension"] }
                },
            new(1002, "Ann Lowe", Role.Patient, [1], "Patient, North (Dr Okafor): her own visits, sensitive ones too.")
                { Born = 1980, Sex = 'F', Gp = 202 },
            new(2001, "Omar Haddad", Role.Patient, [2], "Patient, South (Dr Lee): his own visits.")
                { Born = 1990, Sex = 'M', Gp = 203 }
        };
        for (var i = 0; i < 197; i++)
        {
            var north = i < 117;
            var id = north ? 1003 + i : 2002 + (i - 117);
            var woman = random.Next(2) == 0;
            var first = woman ? women[random.Next(women.Length)] : men[random.Next(men.Length)];
            var name = $"{first} {surnames[random.Next(surnames.Length)]}";
            patients.Add(new Person(id, name, Role.Patient, [north ? 1 : 2], "")
            {
                Born = (short)random.Next(1935, 2021), Sex = woman ? 'F' : 'M',
                Gp = north ? random.Next(2) == 0 ? 201 : 202 : 203
            });
        }

        return [.. patients];
    }

    private static Device[] MakeDevices()
    {
        var random = new Random(20261016);
        var registered = new DateTime(2026, 3, 3, 9, 0, 0);
        List<Device> devices = [];

        string Token(bool bad = false)
        {
            Span<byte> bytes = stackalloc byte[32];
            random.NextBytes(bytes);
            var hex = Convert.ToHexStringLower(bytes);
            return bad ? "dead" + hex[4..] : hex; // (the last mile refuses a token it no longer knows: "dead…", here)
        }

        void Add(Person person, string type, string label, bool bad = false)
        {
            devices.Add(new Device(devices.Count + 1, person.Id, person.User, person.Sites[0], type, label, Token(bad), true,
                registered.AddDays(random.Next(200))));
        }

        foreach (var patient in People.Patients)
            switch (patient.Id)
            {
                case 1001:
                    Add(patient, "ios", "iPhone");
                    Add(patient, "web", "Web");
                    break;
                case 1002:
                    Add(patient, "ios", "iPad", bad: true);
                    Add(patient, "ios", "iPhone");
                    break;
                default:
                    if (random.Next(10) < 8) Add(patient, random.Next(2) == 0 ? "ios" : "android", "Phone");
                    break;
            }

        foreach (var carer in People.Carers) Add(carer, "android", "Android");
        return [.. devices];
    }
}
