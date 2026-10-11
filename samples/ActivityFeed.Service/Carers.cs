namespace ActivityFeed.Service;

/// <summary>A carer's designation: whom they care for, and in what scope.</summary>
public sealed record Designation(int Carer, int Patient, string Scope)
{
    /// <summary>
    ///     What the scope shows: <c>admin</c>, the patient's schedule and messages, but no visit that's sensitive;
    ///     <c>clinical</c>, besides, their sections that aren't sensitive (a result's once released); <c>sensitive</c>,
    ///     everything the patient sees. Hidden, not masked: what a scope doesn't show isn't the carer's at all.
    /// </summary>
    public bool Shows(bool sensitive, bool section, bool released)
    {
        if (section && Scope == "admin" || section && !released) return false;
        return !sensitive || Scope == "sensitive";
    }
}

/// <summary>
///     Who cares for whom, and what each may see: the service's own record (designations, kept as the patients and the
///     practice make them), expanded into grant rows (<c>activity_grants</c> and <c>section_grants</c>) as the patient's
///     rows are written, and taken back, every one, when a designation is revoked. Owned by the provider's loop.
/// </summary>
internal sealed class Carers
{
    private readonly Dictionary<int, Dictionary<long, bool>> _activities = []; // by patient: each activity, of a sensitive visit?
    private readonly List<Designation> _active = [];
    private readonly Dictionary<int, Dictionary<long, Section>> _sections = []; // by patient
    private readonly List<(Designation Designation, DateTime Revoked)> _revoked = []; // (the record of who cared, until when)

    public Carers()
    {
        foreach (var carer in People.Carers)
        foreach (var (patient, scope) in carer.Cares)
            _active.Add(new Designation(carer.Id, patient, scope));
    }

    /// <summary>A patient's carers, as designated.</summary>
    public IEnumerable<Designation> Of(int patient)
    {
        return _active.Where(d => d.Patient == patient);
    }

    /// <summary>Whom a carer cares for.</summary>
    public IEnumerable<Designation> For(int carer)
    {
        return _active.Where(d => d.Carer == carer);
    }

    /// <summary>An activity of a patient's, written: granted to each carer whose scope shows it.</summary>
    public void Written(Activity activity, bool sensitive, Writes writes)
    {
        if (!_activities.TryGetValue(activity.PatientId, out var held)) _activities[activity.PatientId] = held = [];
        held[activity.Seq] = sensitive;
        foreach (var designation in Of(activity.PatientId))
            if (designation.Shows(sensitive, false, true))
                writes.Grant(ActivityGrant.Of(activity.Seq, designation.Carer));
    }

    /// <summary>A section of a patient's, written (a result released, say): granted, or taken back, as each scope shows it.</summary>
    public void Written(Section section, Writes writes)
    {
        if (!_sections.TryGetValue(section.PatientId, out var held)) _sections[section.PatientId] = held = [];
        held[section.Seq] = section;
        foreach (var designation in Of(section.PatientId))
            if (designation.Shows(section.IsSensitive, true, section.Released))
                writes.Grant(SectionGrant.Of(section.Seq, designation.Carer));
            else
                writes.Revoke(SectionGrant.Of(section.Seq, designation.Carer));
    }

    /// <summary>A designation revoked: every row it granted taken back, at once.</summary>
    public Designation? Revoke(int patient, int carer, DateTime now, Writes writes)
    {
        if (_active.FirstOrDefault(d => d.Patient == patient && d.Carer == carer) is not { } designation) return null;
        _active.Remove(designation);
        _revoked.Add((designation, now));
        foreach (var (seq, sensitive) in _activities.GetValueOrDefault(patient) ?? [])
            if (designation.Shows(sensitive, false, true))
                writes.Revoke(ActivityGrant.Of(seq, carer));
        foreach (var (seq, section) in _sections.GetValueOrDefault(patient) ?? [])
            if (designation.Shows(section.IsSensitive, true, section.Released))
                writes.Revoke(SectionGrant.Of(seq, carer));
        return designation;
    }
}
