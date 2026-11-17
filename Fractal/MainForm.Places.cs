using Fractal.Deep;

namespace Fractal;

/// <summary>
/// Places you've been (L shows them, Backspace goes back to the last one).
///
/// A view becomes a place once a full render of it is on screen and you either stay
/// <see cref="PlaceDwellSeconds"/> or change its colours. It is recorded when you move away
/// (or close the app), with the colouring as you left it and a thumbnail shrunk from the image
/// on screen, so nothing is rendered again. The views a zoom or drag passes through, and the
/// steps of a dive, never stay put long enough to count.
/// </summary>
public sealed partial class MainForm
{
    private const double PlaceDwellSeconds = 5;
    private const int ThumbnailWidth = 768; // a fifth of a 4K window (shown smaller); never wider than the window

    private PlaceStore _places = null!;
    private double? _placeArrived; // when a full render of the current view landed; null = not at a place yet
    private bool _placeTouched;    // colours changed here

    private void LoadPlaces()
    {
        _places = PlaceStore.Load(PlaceStore.DefaultFolder, out string? error);
        if (error != null) ShowToast("Couldn't read your places: " + error, 4);
        _view.Moving += OnLeavingPlace;
    }

    /// <summary>A full render landed: if it shows the current view, the view is now a place (if you stay).</summary>
    private void NotePlaceRendered(Field target)
    {
        if (_dive.Active || target.View != _view.Current()) return;
        _placeArrived ??= _clock.Elapsed.TotalSeconds;
    }

    /// <summary>Colours changed here: that makes it a place without waiting.</summary>
    private void TouchPlace() => _placeTouched = true;

    /// <summary>True once the current view counts as a place.</summary>
    private bool AtPlace =>
        _placeArrived is double t && (_placeTouched || _clock.Elapsed.TotalSeconds - t >= PlaceDwellSeconds);

    private void OnLeavingPlace()
    {
        bool record = AtPlace;
        _placeArrived = null;
        _placeTouched = false;
        if (record) RecordPlace();
    }

    /// <summary>On closing: the view you're on is kept, however briefly you were there.</summary>
    private void RecordPlaceOnClose()
    {
        if (_placeArrived != null) RecordPlace();
    }

    /// <summary>Adds the current view (as on screen now) to the places.</summary>
    private void RecordPlace()
    {
        var v = _view.Current();
        var shown = _baseLayer.Field?.View;
        if (shown is not { } f || !f.SameGrid(v) || f.PanX != v.PanX || f.PanY != v.PanY) return;

        var (x, y) = _view.CenterText(v);
        var palette = _colours.Selected;
        var place = new Place
        {
            Re = x,
            Im = y,
            Zoom = _view.Zoom,
            Iterations = _view.MaxIterations,
            Palette = palette.Name,
            PaletteMap = _colours.IsGenerated(palette) ? palette.ToMapText() : null,
            Mapping = _colours.Mapping,
            Density = _colours.Density,
            Offset = _colours.Offset,
            Visited = DateTime.UtcNow,
        };
        try
        {
            using var thumb = _baseLayer.CreateThumbnail(ThumbnailWidth);
            _places.Add(place, thumb);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            ShowToast("Couldn't save this place: " + ex.Message, 4);
        }
    }

    private void ShowPlaces()
    {
        // The view you're on goes in the list first, if it counts, so it's there to come back to.
        // (Leaving it later records it again, which just updates the entry.)
        if (AtPlace) RecordPlace();
        using var dlg = new PlacesDialog(_places, Bounds);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Chosen is { } place) GoToPlace(place);
    }

    /// <summary>Takes the view you're on out of the places (if listed), and doesn't record it when you leave.</summary>
    private void ForgetPlace()
    {
        _placeArrived = null;
        _placeTouched = false;
        var (x, y) = _view.CenterText(_view.Current());
        try
        {
            bool removed = _places.RemoveNear(x, y, _view.Zoom);
            ShowToast(removed ? "Removed this place from your places" : "This place won't be saved", 2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast("Couldn't update your places: " + ex.Message, 4);
        }
    }

    /// <summary>Back to the newest place that isn't where you are now.</summary>
    private void GoBack()
    {
        var v = _view.Current();
        var (x, y) = _view.CenterText(v);
        var target = _places.Places.FirstOrDefault(p => !PlaceStore.IsNear(p, x, y, _view.Zoom));
        if (target == null) { ShowToast("No earlier place to go back to yet"); return; }
        GoToPlace(target);
        ShowToast($"Back to zoom {target.Zoom:G3}x");
    }

    private void GoToPlace(Place p)
    {
        StopDive();
        double scale = _view.ScaleForZoom(p.Zoom);
        int bits = BigFixed.BitsFor(scale);
        BigFixed re, im;
        try
        {
            re = BigFixed.Parse(p.Re, bits);
            im = BigFixed.Parse(p.Im, bits);
        }
        catch (FormatException)
        {
            ShowToast("That place's position is damaged", 3);
            return;
        }
        _view.GoTo(re, im, scale); // records the place you're leaving
        _view.SetIterations(p.Iterations);
        if (!_colours.Restore(p.Palette, p.PaletteMap))
            ShowToast($"Palette \"{p.Palette}\" isn't available any more; kept {_colours.Selected.Name}", 3);
        _colours.Mapping = p.Mapping;
        _colours.SetDensity(p.Density);
        _colours.SetOffset(p.Offset);
        Recolor();
        RequestRender();
    }
}
