namespace ActivityFeed.Service;

/// <summary>A patient's consent: a GP may read their sections, of everything or of a tag's problems, until withdrawn.</summary>
public sealed record Consent(long Id, int Patient, int Gp, string Label, string[] Problems, bool Everything, DateTime Since)
{
    /// <summary>Whether it covers a section's problem: everything (sensitive ones named too), or one of its tag's.</summary>
    public bool Covers(int problem)
    {
        return Everything || Problems.Contains(Simulator.TitleOf(problem));
    }
}

/// <summary>
///     Who each section is shared with, from the patients' consents: the service's own record, expanded into grant rows
///     (<c>section_grants</c>) as sections are written and as consents change. Registering with a GP records a consent of
///     the patient's choosing: here, everything, each sensitive problem named too, so the registered GP reads the
///     patient's history as today. A patient may share a tag of theirs (the problems it groups) with another GP, and
///     withdraw any consent, their registration's too: a section's grant rows follow at once, its history kept. Owned by
///     the provider's loop.
/// </summary>
internal sealed class Consents
{
    private readonly List<Consent> _active = [];
    private readonly List<(Consent Consent, DateTime Withdrawn)> _withdrawn = []; // (the record of who was shared what, when)
    private readonly Dictionary<long, (Section Section, HashSet<int> Gps)> _sections = [];
    private readonly Dictionary<int, List<long>> _sectionsOf = []; // by patient
    private long _next;

    public Consents()
    {
        foreach (var patient in People.Patients) // (each registration's consent: everything, to the registered GP)
            _active.Add(new Consent(++_next, patient.Id, patient.Gp, "Everything", [], true, DateTime.MinValue));
    }

    /// <summary>A patient's consents, as they stand.</summary>
    public IEnumerable<Consent> Of(int patient)
    {
        return _active.Where(c => c.Patient == patient);
    }

    /// <summary>A section written: its grant rows brought to what the consents say of it.</summary>
    public void Written(Section section, Writes writes)
    {
        if (!_sections.ContainsKey(section.Seq))
        {
            if (!_sectionsOf.TryGetValue(section.PatientId, out var list)) _sectionsOf[section.PatientId] = list = [];
            list.Add(section.Seq);
        }

        Apply(section, writes);
    }

    /// <summary>A tag of the patient's shared with a GP: a consent, and the grant rows of every section it covers.</summary>
    public Consent Share(Person patient, int gp, string tag, DateTime now, Writes writes)
    {
        var consent = new Consent(++_next, patient.Id, gp, tag, patient.Tags[tag], false, now);
        _active.Add(consent);
        Recompute(patient.Id, writes);
        return consent;
    }

    /// <summary>A consent withdrawn: its grant rows gone, but for what another consent of the patient's still covers.</summary>
    public Consent? Withdraw(int patient, long id, DateTime now, Writes writes)
    {
        if (_active.FirstOrDefault(c => c.Id == id && c.Patient == patient) is not { } consent) return null;
        _active.Remove(consent);
        _withdrawn.Add((consent, now));
        Recompute(patient, writes);
        return consent;
    }

    private void Recompute(int patient, Writes writes)
    {
        foreach (var seq in _sectionsOf.GetValueOrDefault(patient) ?? []) Apply(_sections[seq].Section, writes);
    }

    private void Apply(Section section, Writes writes)
    {
        var shared = _active.Where(c => c.Patient == section.PatientId && c.Covers(section.ProblemId)).Select(c => c.Gp)
            .ToHashSet();
        var held = _sections.TryGetValue(section.Seq, out var was) ? was.Gps : [];
        foreach (var gp in shared.Except(held)) writes.Grant(SectionGrant.Of(section.Seq, gp));
        foreach (var gp in held.Except(shared)) writes.Revoke(SectionGrant.Of(section.Seq, gp));
        _sections[section.Seq] = (section, shared);
    }
}
