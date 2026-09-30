#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.UI;

// What a screen's bindings read (docs/design/13 "As built (style and layout records)", issue #96): a
// class whose public fields and properties are the paths a layout binds — `"bind": "items"`,
// `"args": { "current": "weight" }` — read through delegates compiled once per type, never by
// reflection per frame. It is an entry of the `view_model` vocabulary (#28), declared rather than
// registered, for the plugin that owns it:
//
//   [ViewModel("inventory")]
//   public sealed class InventoryView : IViewModel
//   {
//       public List<ItemRow> Items { get; } = new();
//       public float Weight;
//       public void Refresh(in UiBindContext context) { … read the player's bag into Items … }
//   }
//
// A `screen` record names it by id, and each screen opened makes a new one (parameterless constructor).
[Vocabulary(UiScreens.ViewModelVocabulary, Key = "viewModel")]
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public interface IViewModel
{
    // Called by UiScreen.Refresh before its bindings are read: bring what they read up to date from the
    // world. Runs every frame a screen is shown, so, once warm, it should not allocate either.
    void Refresh(in UiBindContext context) { }
}

[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class ViewModelAttribute : VocabularyEntryAttribute<IViewModel>
{
    public ViewModelAttribute(string id) : base(id) { }
}

// A screen record, opened: its layout built, its view-model made, its bindings read on Refresh. Put
// Root in a UiRoot (root.Content.Add(screen.Root)); when content reloads — the layout, a style, a string
// table — the screen builds itself again and puts the new tree where the old one was.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiScreen
{
    private readonly UiScreens _owner;

    internal UiScreen(UiScreens owner, RecordId id, ScreenRecord record, IViewModel? viewModel, UiView view, UiBindContext context)
    {
        _owner = owner;
        Id = id;
        Record = record;
        ViewModel = viewModel;
        View = view;
        Context = context;
        Builds = 1;
    }

    public RecordId Id { get; }
    public ScreenRecord Record { get; private set; }
    public IViewModel? ViewModel { get; }
    public UiView View { get; private set; }
    public Widget Root => View.Root;

    // The world `visibleIf` asks about, and who: the player, usually.
    public UiBindContext Context { get; set; }

    public bool IsOpen { get; private set; } = true;

    // How many times its tree was built: 1, plus one per reload that changed it.
    public int Builds { get; private set; }

    // The view-model's Refresh, then every binding and condition. Allocates nothing once built, when
    // nothing it reads changed.
    public void Refresh()
    {
        var context = Context;
        ViewModel?.Refresh(in context);
        View.Refresh(ViewModel, in context);
    }

    // Takes Root out of its parent, and stops rebuilding on reload.
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Detach(Root);
        _owner.Closed(this);
    }

    // Builds the tree again from the records as they are now, and puts it where the old one was.
    internal void Rebuild(ScreenRecord record, UiView view)
    {
        var old = View.Root;
        var parent = old.Parent;
        Record = record;
        View = view;
        Builds++;
        switch (parent)
        {
            case Container container:
                int index = container.IndexOf(old);
                container.Remove(old);
                container.Insert(index, view.Root);
                break;
            case Scroll scroll:
                scroll.Content = view.Root;
                break;
        }
        Refresh();
    }

    private static void Detach(Widget widget)
    {
        switch (widget.Parent)
        {
            case Container container: container.Remove(widget); break;
            case Scroll scroll: scroll.Content = null; break;
        }
    }
}

// Builds layouts and opens screens from records, and rebuilds the open ones when content changes.
// UiModule provides it (ctx.Get<UiScreens>() in a module that depends on UiModule, or a world's
// resources).
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiScreens
{
    public const string ViewModelVocabulary = "view_model";

    private readonly RecordStore _records;
    private readonly Vocabulary<IViewModel> _viewModels;
    private readonly LayoutBuilder _builder;
    private readonly List<UiScreen> _open = new();

    internal UiScreens(RecordStore records, Vocabulary<IViewModel> viewModels, UiStyles styles, Localisation text)
    {
        _records = records;
        _viewModels = viewModels;
        _builder = new LayoutBuilder(styles, text);
    }

    public IReadOnlyList<UiScreen> Open => _open;

    // A layout's widgets, with nothing bound yet (Refresh with a source to bind them). Not rebuilt on
    // reload: UiScreen is what follows content.
    public UiView BuildLayout(RecordId layout) => _builder.Build(layout, _records.Get<UiLayoutRecord>(layout));

    // Opens a screen record: builds its layout and makes its view-model, and reads them once.
    public UiScreen OpenScreen(RecordId screen, UiBindContext context = default)
    {
        var record = _records.Get<ScreenRecord>(screen);
        var viewModel = record.ViewModel.Length > 0 ? _viewModels.Create(record.ViewModel) : null;
        var opened = new UiScreen(this, screen, record, viewModel, BuildLayout(record.Layout), context);
        _open.Add(opened);
        opened.Refresh();
        return opened;
    }

    internal void Closed(UiScreen screen) => _open.Remove(screen);

    // Content changed (a record file, a string table, the language): every open screen is built again.
    // One whose screen or layout record is gone keeps what it has, and says so.
    internal void RebuildAll()
    {
        foreach (var screen in _open.ToArray())
        {
            if (!_records.TryGet(screen.Id, out ScreenRecord record) || !_records.TryGet(record.Layout.Id, out UiLayoutRecord layout))
            {
                Log.Warn(LogCat.UI, $"screen {screen.Id}: its record or layout is gone; it keeps what it showed");
                continue;
            }
            screen.Rebuild(record, _builder.Build(record.Layout.Id, layout));
        }
    }
}
