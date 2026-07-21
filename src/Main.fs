module SvgExtrude.Main

open System
open Fable.Core
open Fable.Core.JsInterop
open Browser.Dom
open Browser.Types
open SvgExtrude.Types
open SvgExtrude.TextShapes

[<Emit("parseFloat($0)")>]
let private parseFloatJs (s: string) : float = jsNative

[<Emit("$0.arrayBuffer().then($1)")>]
let private readFileBuffer (file: obj) (cb: obj -> unit) : unit = jsNative

[<Emit("$0.then($1)")>]
let private thenDo (p: obj) (cb: obj -> unit) : unit = jsNative

[<Import("canPickFolder", "./fs-helpers.js")>]
let private canPickFolder () : bool = jsNative

[<Import("pickFolder", "./fs-helpers.js")>]
let private pickFolder () : JS.Promise<obj> = jsNative

[<Import("saveToFolder", "./fs-helpers.js")>]
let private saveToFolder (handle: obj) (filename: string) (buffer: obj) : JS.Promise<bool> = jsNative

let private byId (id: string) : HTMLElement = document.getElementById id
let private inputById (id: string) : HTMLInputElement = byId id :?> HTMLInputElement

// ---------------------------------------------------------------------------
// State (sliders hold their own values; these mirror them for the pipeline)
// ---------------------------------------------------------------------------

let private fonts = ResizeArray<string * obj>()
let mutable private nameFont: obj option = None
let mutable private letterFont: obj option = None
let mutable private nameText = "Emma"
let mutable private letterText = "E"
let mutable private nameSize = 35.0
let mutable private letterSize = 100.0
let mutable private letterSpacing = 0.0
/// Clearance between the name outline and the letter's cutout walls, mm.
let mutable private fitGap = 0.2
let mutable private holeFill = 2.0
/// Name centre relative to the letter centre (the letter sits at the origin).
let mutable private offX = 0.0
let mutable private offY = 0.0
let mutable private letterH = 5.0
let mutable private nameH = 6.0
let mutable private letterColor = "#7fd4c4"
let mutable private nameColor = "#f6b8d0"
let mutable private folderHandle: obj = null
let mutable private viewer: obj = null

// Pipeline caches: text layout + glyph flattening survive placement-only
// changes (offset drag, gap, heights) so those stay cheap.
let mutable private nameShapesCache: Shape list = []   // centered at origin
let mutable private letterShapesCache: Shape list = [] // centered at origin
let mutable private letterPositions: float array = [||]
let mutable private namePositions: float array = [||]
let mutable private nameCenterActual = { X = 0.0; Y = 0.0 }
let mutable private nameRadiusActual = 0.0

/// Curve flattening tolerance for glyph outlines, in mm.
let private glyphTol = 0.02

// ---------------------------------------------------------------------------
// Geometry pipeline
// ---------------------------------------------------------------------------

let private updateReadouts () =
    let sizeEl = byId "size-readout"
    let triEl = byId "tri-readout"
    if letterPositions.Length = 0 && namePositions.Length = 0 then
        sizeEl.textContent <- "—"
        triEl.textContent <- "—"
    else
        let mutable minX = infinity
        let mutable minY = infinity
        let mutable maxX = -infinity
        let mutable maxY = -infinity
        let mutable maxZ = 0.0
        let scan (positions: float array) =
            let mutable i = 0
            while i < positions.Length do
                let x = positions.[i]
                let y = positions.[i + 1]
                let z = positions.[i + 2]
                if x < minX then minX <- x
                if y < minY then minY <- y
                if x > maxX then maxX <- x
                if y > maxY then maxY <- y
                if z > maxZ then maxZ <- z
                i <- i + 3
        scan letterPositions
        scan namePositions
        sizeEl.textContent <- sprintf "%.1f × %.1f × %.1f mm" (maxX - minX) (maxY - minY) maxZ
        triEl.textContent <- string ((letterPositions.Length + namePositions.Length) / 9)

let private updateExportState () =
    let has = letterPositions.Length > 0 || namePositions.Length > 0
    (inputById "export-btn").disabled <- not has
    (inputById "export-combined").disabled <- not has
    (inputById "export-separate").disabled <- not has
    (byId "viewer-hint")?style?display <- if has then "none" else ""

let mutable private firstBuild = true

/// Rebuild the meshes from the cached (origin-centered) letter + name shapes:
/// place the name at its offset, carve the fit-gap pocket out of the letter,
/// extrude both. Runs on offset/gap/height changes — no text layout here.
let private rebuildAssembly () =
    let placedName =
        nameShapesCache
        |> List.map (Geometry.mapShape (fun p -> { X = p.X + offX; Y = p.Y + offY }))

    // The letter piece: monogram minus the name outline dilated by the fit
    // gap. The separately printed name then slides into the pocket with
    // `fitGap` mm of clearance all round, ready for glue. Dilating only the
    // outer rings (holes ignored) means no stranded letter islands inside
    // name counters.
    let letterPiece =
        if letterShapesCache.IsEmpty then []
        elif placedName.IsEmpty then letterShapesCache
        else
            let cutter =
                placedName
                |> List.map (fun s -> s.Outer)
                |> Array.ofList
                |> fun outers -> Clipper.offsetUnion outers fitGap
            Clipper.toShapes (Clipper.combine (Clipper.shapeRings letterShapesCache) cutter "difference")

    let extrudeAll (shapes: Shape list) (height: float) : float array =
        if shapes.IsEmpty then [||]
        else
            let acc = ResizeArray<float>()
            for s in shapes do
                let p, _ = Geometry.extrude s height
                acc.AddRange p
            acc.ToArray()
    letterPositions <- extrudeAll letterPiece letterH
    namePositions <- extrudeAll placedName nameH

    // Name drag zone: bounding circle of the placed name.
    match Geometry.bounds placedName with
    | Some (minX, minY, maxX, maxY) ->
        nameCenterActual <- { X = (minX + maxX) / 2.0; Y = (minY + maxY) / 2.0 }
        let w = maxX - minX
        let h = maxY - minY
        nameRadiusActual <- sqrt (w * w + h * h) / 2.0
    | None -> nameRadiusActual <- 0.0

    if not (isNull viewer) then
        if letterPositions.Length > 0 then Viewer.setMesh viewer "letter" letterPositions letterColor
        else Viewer.removeMesh viewer "letter"
        if namePositions.Length > 0 then Viewer.setMesh viewer "name" namePositions nameColor
        else Viewer.removeMesh viewer "name"
    updateReadouts ()
    updateExportState ()
    if firstBuild && (letterPositions.Length > 0 || namePositions.Length > 0) && not (isNull viewer) then
        firstBuild <- false
        Viewer.fitView viewer

/// Lay out a string with the font's own metrics, flatten, merge overlapping
/// strokes (script fonts!), apply the hole-fill threshold, and center the
/// result on the origin (y flipped from font space to y-up).
let private buildTextShapes (font: obj) (txt: string) (size: float) (spacing: float) : Shape list =
    let trimmed = txt.Trim ()
    if trimmed = "" then []
    else
        // Holes are classified glyph by glyph (an 'm' overlapping a script E
        // must not read as a hole of the E), then everything is unioned with
        // a 0.05mm weld: script glyphs often only *touch* at a point (loop
        // meeting a stem); the weld makes those joints real so the mesh is
        // manifold and the printed piece can't hinge apart.
        let shapes =
            TextShapes.textCommands font trimmed size spacing
            |> Array.toList
            |> List.collect (TextShapes.commandShapes glyphTol 0.0)
            |> fun raw -> Clipper.toShapes (Clipper.unionWeld (Clipper.shapeRings raw) 0.05)
            |> List.map (fun s ->
                { s with Holes = s.Holes |> List.filter (fun h -> abs (Rings.signedArea h) >= holeFill) })
        match Geometry.bounds shapes with
        | Some (minX, minY, maxX, maxY) ->
            let cx = (minX + maxX) / 2.0
            let cy = (minY + maxY) / 2.0
            shapes |> List.map (Geometry.mapShape (fun p -> { X = p.X - cx; Y = cy - p.Y }))
        | None -> []

/// Full rebuild: re-run text layout for both pieces, then the assembly.
let private rebuildText () =
    nameShapesCache <-
        match nameFont with
        | Some f -> buildTextShapes f nameText nameSize letterSpacing
        | None -> []
    letterShapesCache <-
        match letterFont with
        | Some f -> buildTextShapes f letterText letterSize 0.0
        | None -> []
    rebuildAssembly ()

// Debounced schedulers: text-affecting changes re-run the whole pipeline,
// placement-only changes skip layout + glyph flattening.
let mutable private textQueued = false
let mutable private meshQueued = false

let private scheduleText () =
    if not textQueued then
        textQueued <- true
        window.setTimeout ((fun () ->
            textQueued <- false
            rebuildText ()), 70)
        |> ignore

let private scheduleMeshes () =
    if not meshQueued then
        meshQueued <- true
        window.setTimeout ((fun () ->
            meshQueued <- false
            rebuildAssembly ()), 40)
        |> ignore

// ---------------------------------------------------------------------------
// Export
// ---------------------------------------------------------------------------

let private safeName () =
    let source =
        if nameText.Trim () <> "" then nameText.Trim ()
        elif letterText.Trim () <> "" then letterText.Trim ()
        else "door-sign"
    let sb = System.Text.StringBuilder ()
    for c in source do
        sb.Append (if "\\/:*?\"<>|".Contains (string c) then '-' else c) |> ignore
    sb.ToString ()

let private saveStl (filename: string) (buf: obj) =
    let note = byId "export-note"
    if isNull folderHandle then
        Stl.download filename buf
        note.textContent <- sprintf "Saved %s (download)" filename
    else
        thenDo (saveToFolder folderHandle filename buf) (fun _ ->
            note.textContent <- sprintf "Saved %s to folder" filename)

let private exportCombined () =
    if letterPositions.Length > 0 || namePositions.Length > 0 then
        saveStl (safeName () + ".stl") (Stl.build [ letterPositions; namePositions ])

let private exportSeparate () =
    if letterPositions.Length > 0 then
        saveStl (safeName () + "-letter.stl") (Stl.build [ letterPositions ])
    if namePositions.Length > 0 then
        saveStl (safeName () + "-name.stl") (Stl.build [ namePositions ])

// ---------------------------------------------------------------------------
// UI wiring
// ---------------------------------------------------------------------------

let private renderFontSelects () =
    let render (id: string) (active: obj option) =
        let sel = byId id :?> HTMLSelectElement
        sel.innerHTML <-
            fonts
            |> Seq.mapi (fun i (name, f) ->
                let selected = (active = Some f)
                sprintf "<option value=\"%d\"%s>%s</option>" i (if selected then " selected" else "") name)
            |> String.concat ""
    render "name-font" nameFont
    render "letter-font" letterFont

let private bindFontSelect (id: string) (apply: obj -> unit) =
    (byId id).addEventListener (
        "change",
        fun _ ->
            let i = int (parseFloatJs (inputById id).value)
            if i >= 0 && i < fonts.Count then
                apply (snd fonts.[i])
                scheduleText ()
    )

let private bindSlider (id: string) (fmt: float -> string) (apply: float -> unit) =
    let inp = inputById id
    let valEl = byId (id + "-val")
    let update () =
        let v = parseFloatJs inp.value
        if not (Double.IsNaN v) then
            valEl.textContent <- fmt v
            apply v
    inp.addEventListener ("input", fun _ -> update ())
    update ()

/// Set the name offset programmatically (drag / test hook) and reflect it
/// back into the sliders so drag and sliders stay in sync.
let private setOffset (x: float) (y: float) =
    offX <- Math.Round (x * 10.0) / 10.0
    offY <- Math.Round (y * 10.0) / 10.0
    (inputById "off-x").value <- string offX
    (inputById "off-y").value <- string offY
    (byId "off-x-val").textContent <- sprintf "%.1f mm" offX
    (byId "off-y-val").textContent <- sprintf "%.1f mm" offY

let private defaults = [
    "name-size", "35"; "letter-size", "100"; "letter-spacing", "0"
    "off-x", "0"; "off-y", "0"; "fit-gap", "0.2"; "hole-fill", "2"
    "letter-h", "5"; "name-h", "6" ]

let private fmtFor (id: string) (v: float) =
    match id with
    | "fit-gap" -> sprintf "%.2f mm" v
    | "hole-fill" -> sprintf "%.1f mm²" v
    | _ -> sprintf "%.1f mm" v

let private init () =
    viewer <- Viewer.createViewer (byId "viewer-box")

    // Drag the name across the letter in the preview; drags write back into
    // the offset sliders.
    Viewer.registerDrag
        viewer
        (fun () ->
            if nameRadiusActual <= 0.0 then null
            else createObj [ "X" ==> nameCenterActual.X; "Y" ==> nameCenterActual.Y; "R" ==> nameRadiusActual ])
        (fun p ->
            setOffset (p?x) (p?y)
            scheduleMeshes ())

    // Font upload: uploaded fonts join both dropdowns; the newest upload is
    // selected for the name (custom scripts are the common case).
    let fontInput = inputById "font-files"
    (byId "font-btn").addEventListener ("click", fun _ -> fontInput.click ())
    fontInput.addEventListener (
        "change",
        fun _ ->
            let files: obj = fontInput?files
            let n: int = files?length
            for k in 0 .. n - 1 do
                let file: obj = files?item (k)
                let name: string = !!(file?name)
                readFileBuffer file (fun buf ->
                    try
                        let font = TextShapes.parseFontBuffer buf
                        fonts.Add (TextShapes.fontName font, font)
                        nameFont <- Some font
                        renderFontSelects ()
                        scheduleText ()
                    with _ ->
                        window.alert (sprintf "Could not read %s as a font (TTF/OTF/WOFF)." name))
            fontInput.value <- ""
    )
    bindFontSelect "name-font" (fun f -> nameFont <- Some f)
    bindFontSelect "letter-font" (fun f -> letterFont <- Some f)

    // Save folder (File System Access API; hidden when unsupported).
    if canPickFolder () then
        (byId "folder-btn").addEventListener (
            "click",
            fun _ ->
                thenDo (pickFolder ()) (fun handle ->
                    if not (isNull handle) then
                        folderHandle <- handle
                        (byId "folder-name").textContent <- sprintf "Saving into: %s" (!!(handle?name): string))
        )
    else
        (byId "folder-btn")?style?display <- "none"
        (byId "folder-label")?style?display <- "none"
        (byId "folder-name").textContent <- "Save folder needs Chrome/Edge — exports download normally"

    // Name + letter text.
    let nameInput = inputById "name-input"
    nameInput.addEventListener (
        "input",
        fun _ ->
            nameText <- nameInput.value
            scheduleText ()
    )
    let letterInput = inputById "letter-input"
    letterInput.addEventListener (
        "input",
        fun _ ->
            letterText <- letterInput.value
            scheduleText ()
    )

    // Colors (preview-only refresh).
    (inputById "letter-color").addEventListener (
        "input",
        fun _ ->
            letterColor <- (inputById "letter-color").value
            if not (isNull viewer) then Viewer.setColor viewer "letter" letterColor
    )
    (inputById "name-color").addEventListener (
        "input",
        fun _ ->
            nameColor <- (inputById "name-color").value
            if not (isNull viewer) then Viewer.setColor viewer "name" nameColor
    )

    // Sliders. Text-affecting ones re-run layout; the rest only re-mesh.
    let mm v = sprintf "%.1f mm" v
    bindSlider "name-size" mm (fun v -> nameSize <- v; scheduleText ())
    bindSlider "letter-size" mm (fun v -> letterSize <- v; scheduleText ())
    bindSlider "letter-spacing" mm (fun v -> letterSpacing <- v; scheduleText ())
    bindSlider "hole-fill" (fmtFor "hole-fill") (fun v -> holeFill <- v; scheduleText ())
    bindSlider "off-x" mm (fun v -> offX <- v; scheduleMeshes ())
    bindSlider "off-y" mm (fun v -> offY <- v; scheduleMeshes ())
    bindSlider "fit-gap" (fmtFor "fit-gap") (fun v -> fitGap <- v; scheduleMeshes ())
    bindSlider "letter-h" mm (fun v -> letterH <- v; scheduleMeshes ())
    bindSlider "name-h" mm (fun v -> nameH <- v; scheduleMeshes ())

    // Reset: restore defaults (fonts, folder and texts survive).
    (byId "reset-btn").addEventListener (
        "click",
        fun _ ->
            for (id, v) in defaults do
                (inputById id).value <- v
                (byId (id + "-val")).textContent <- fmtFor id (parseFloatJs v)
            nameSize <- 35.0
            letterSize <- 100.0
            letterSpacing <- 0.0
            offX <- 0.0
            offY <- 0.0
            fitGap <- 0.2
            holeFill <- 2.0
            letterH <- 5.0
            nameH <- 6.0
            scheduleText ()
    )

    (byId "fit-btn").addEventListener ("click", fun _ -> if not (isNull viewer) then Viewer.fitView viewer)
    (byId "export-btn").addEventListener ("click", fun _ -> exportCombined ())
    (byId "export-combined").addEventListener ("click", fun _ -> exportCombined ())
    (byId "export-separate").addEventListener ("click", fun _ -> exportSeparate ())

    // Test hook: deterministic name placement for the e2e suite.
    window?__setNameOffset <- (fun (p: obj) ->
        setOffset (p?x) (p?y)
        rebuildAssembly ())

    // Bundled fonts so the app works with zero setup: script for the name,
    // serif for the monogram letter.
    thenDo (TextShapes.loadBundledFonts ()) (fun entries ->
        let arr: obj array = !!entries
        for e in arr do
            fonts.Add (!!(e?name), e?font)
        if fonts.Count > 0 then nameFont <- Some (snd fonts.[0])
        if fonts.Count > 1 then letterFont <- Some (snd fonts.[1])
        elif fonts.Count > 0 then letterFont <- Some (snd fonts.[0])
        renderFontSelects ()
        scheduleText ())

init ()
