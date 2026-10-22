using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using PdfEditor.Core.Cleaning;

namespace PdfEditor.Views;

public sealed class GroupHeader
{
    public GroupHeader(string title) => Title = title;

    public string Title { get; }
}

/// <summary>List row wrapping a <see cref="Candidate"/>; edits are written straight to the candidate.</summary>
public sealed class CandidateItem : INotifyPropertyChanged
{
    private bool _isCurrent;

    public CandidateItem(Candidate candidate) => Candidate = candidate;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Candidate Candidate { get; }

    public string Label => Candidate.Label;

    public string ConfidenceText => Candidate.Certain ? "Kesin watermark" : "Olası, kontrol edin";

    public Visibility ReplacementVisibility =>
        Candidate.Group == CandidateGroup.Text ? Visibility.Visible : Visibility.Collapsed;

    public bool Selected
    {
        get => Candidate.Selected;
        set
        {
            if (Candidate.Selected == value)
                return;
            Candidate.Selected = value;
            OnPropertyChanged();
        }
    }

    public string Replacement
    {
        get => Candidate.Replacement;
        set
        {
            if (Candidate.Replacement == value)
                return;
            Candidate.Replacement = value;
            OnPropertyChanged();
        }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
                return;
            _isCurrent = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record ReplacementItem(string Old, string New)
{
    public string Display => $"{Old}  →  {(string.IsNullOrEmpty(New) ? "(sil)" : New)}";
}
