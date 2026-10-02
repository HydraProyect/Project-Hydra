using System.Text.RegularExpressions;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// S3 (fase 0 de salvaguardas, 2026-10-02). Cerrar un <c>Drawer</c> o un <c>Modal</c> con la X, Escape o un
/// clic en el fondo mientras su formulario tiene algo escrito pregunta «¿Descartar cambios?» (P1-E2b,
/// decisión del propietario del 2026-09-26). El componente lo hace solo si el formulario le pasa
/// <c>HayCambios</c>, y hasta hoy eso era opcional: un Drawer o Modal con campos que no lo pasaba tiraba lo
/// escrito sin preguntar (D-05, D-20; M13 del análisis de causas raíz). Este test lo vuelve obligatorio:
/// <b>todo <c>&lt;Drawer&gt;</c> o <c>&lt;Modal&gt;</c> (y <c>&lt;DialogoConfirmacion&gt;</c>, que monta un Modal) cuyo cuerpo
/// contiene campos pasa <c>HayCambios</c></b>,
/// salvo que no se pueda descartar (<c>Bloqueante</c> fijo, sin X, sin Escape ni cierre por fondo) o esté en
/// <see cref="ContenedoresSinGuardian"/> con su motivo.
///
/// <para>
/// <b>Complementa, no sustituye,</b> a <c>NavigationLockSoloEnElAvisoDeCambiosSinGuardarTests</c>: aquella
/// lista es positiva («estos ya protegen») y solo crece; esta es la propiedad inversa («ningún contenedor con
/// campos queda sin proteger»), medida sobre cada elemento y no sobre cada fichero, de modo que un segundo
/// Modal sin guardián en un fichero que ya protege el primero también falla.
/// </para>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> «Campo» es <c>CampoTexto</c>, <c>CampoSelect</c>,
/// <c>CampoTextarea</c>, <c>CampoBuscarSelect</c>, <c>SelectorEntidad</c>, <c>SelectorMultiple</c>,
/// <c>ZonaSoltarArchivo</c>, los <c>Input*</c> de Blazor y <c>&lt;input&gt;</c>/<c>&lt;textarea&gt;</c>/
/// <c>&lt;select&gt;</c> crudos (salvo <c>type="hidden"</c>); un control que actúa al instante, como
/// <c>SelectorClienteActivo</c>, no es un campo. El campo se atribuye al contenedor más interior. NO ve: campos
/// dentro de un componente hijo que el Drawer o Modal monta (el test mira el fichero que contiene el
/// <c>&lt;Drawer&gt;</c>; el formulario hijo con su propio <c>HayCambios</c> es del hijo); que
/// <c>HayCambios</c> sea correcto (solo rechaza el literal <c>() =&gt; false</c>, no una función que olvide
/// un campo: eso lo prueba el bUnit de cada formulario); ni los formularios a página completa, que no son
/// contenedores.
/// </para>
///
/// <para>
/// <b>«Cancelar» cierra como la X (D-05, 2026-10-02).</b> Si el contenedor pasa un <c>HayCambios</c> real, el botón
/// «Cancelar» o «Descartar» de su <c>&lt;Pie&gt;</c> (no el primario ni el destructivo, que son la acción del diálogo; no uno del cuerpo,
/// que repliega un subformulario) llama a <c>SolicitarCierreAsync</c> del propio Drawer o Modal, en su <c>OnClick</c> o en un
/// manejador del <c>.razor</c>/<c>.razor.cs</c> a un solo salto. NO ve: un «Cancelar» con otro rótulo («Volver», «Cerrar»), un
/// manejador que delega en otro manejador, ni que la referencia (<c>@ref</c>) apunte al contenedor correcto.
/// </para>
/// </summary>
public class DrawerYModalConCamposPreguntanAlDescartarTests
{
    private const string Contenedores = "Drawer|Modal|DialogoConfirmacion";

    private const string Campos =
        "CampoTexto|CampoSelect|CampoTextarea|CampoBuscarSelect|SelectorEntidad|SelectorMultiple|ZonaSoltarArchivo|" +
        "InputText|InputTextArea|InputNumber|InputSelect|InputDate|InputCheckbox|InputFile|textarea|select|input";

    private static readonly Regex AtributoHayCambios = new(@"(?:^|\s)HayCambios\s*=\s*""", RegexOptions.Compiled);

    // «Bloqueante» a secas o ="true": no hay X, ni Escape, ni cierre por el fondo, así que no hay nada que preguntar.
    // Bloqueante="_guardando" (dinámico) no vale: fuera de ese instante, el contenedor se puede cerrar.
    private static readonly Regex BloqueanteFijo = new(@"(?:^|\s)Bloqueante(?:\s*=\s*""(?:true|True)"")?(?=[\s/>])", RegexOptions.Compiled);

    // Guardianes que nunca preguntan, sin espacios y sin la arroba de Razor: «false», «() => false», «(() => false)» y
    // «() => { return false; }». No intenta ser exhaustivo (una función que olvide un campo es del bUnit de cada formulario).
    private static readonly string[] GuardianesInertes = ["false", "()=>false", "(()=>false)", "()=>{returnfalse;}"];

    /// <summary>
    /// Drawer o Modal con campos que no pasa <c>HayCambios</c>, con el motivo. Clave: ruta desde la raíz del
    /// repositorio más <c>#Modal1</c>, <c>#Drawer2</c>… (orden de aparición en el fichero). Solo se añade con
    /// una razón escrita; el resto de contenedores con campos pasa <c>HayCambios</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ContenedoresSinGuardian = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["src/CaeManager.Web/Features/Plataforma/Pages/Plataforma.razor#Modal1"] =
            "Confirmación del acto fundacional: su única «entrada» es una casilla de «Entiendo…» que habilita el botón. " +
            "No hay dato que se pierda al cerrar; cerrar y volver a abrir es gratis.",

        // Los tres diálogos de confirmación de Visitas (cancelar, reactivar, cancelar en lote) llevan un «Motivo
        // (opcional)»: es el cuerpo de una confirmación, no un formulario; «Volver» y la X lo descartan a propósito.
        // Si se decide proteger el motivo, DialogoConfirmacion necesita un HayCambios que pase a su Modal.
        ["src/CaeManager.Web/Features/Visitas/Pages/Visitas.razor#DialogoConfirmacion1"] = MotivoOpcionalDeConfirmacion,
        ["src/CaeManager.Web/Features/Visitas/Pages/Visitas.razor#DialogoConfirmacion2"] = MotivoOpcionalDeConfirmacion,
        ["src/CaeManager.Web/Features/Visitas/Pages/Visitas.razor#DialogoConfirmacion3"] = MotivoOpcionalDeConfirmacion,
    };

    private const string MotivoOpcionalDeConfirmacion =
        "Diálogo de confirmación con un «Motivo (opcional)» de una línea o dos: la acción es confirmar, el texto es un añadido " +
        "voluntario y se pierde a propósito con «Volver». Excepción abierta a decisión de producto (¿proteger el motivo?).";

    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Todo_Drawer_o_Modal_con_campos_pasa_HayCambios()
    {
        var medidos = MedirTodos();

        // Control positivo: si el recorrido no viera contenedores ni campos, «ninguno sin guardián» valdría por vacío.
        medidos.Count(c => c.Campos > 0).Should().BeGreaterThan(40, "había 56 contenedores con campos al escribirlo; si baja de golpe, dejó de mirar");
        medidos.Count(c => c.TieneGuardian).Should().BeGreaterThan(35, "había ≥ 50 con HayCambios al escribirlo; si baja de golpe, dejó de ver el atributo");

        var problemas = Evaluar(medidos, ContenedoresSinGuardian);

        string.Join(Environment.NewLine, problemas).Should().BeEmpty(
            "un Drawer o Modal con campos pasa HayCambios (con InstantaneaFormulario, p. ej.): cerrarlo con la X, Escape o el fondo " +
            "con algo escrito pregunta «¿Descartar cambios?». Solo si no hay dato que perder se añade a ContenedoresSinGuardian, con su motivo");
    }

    [Fact]
    public void La_lista_de_contenedores_sin_guardian_lleva_motivo_y_apunta_a_algo_que_existe()
    {
        var raiz = MarcadoRazor.RaizDelRepositorio();

        ContenedoresSinGuardian.Should().OnlyContain(kv => kv.Value.Length >= 40, "cada excepción lleva su razón escrita");
        ContenedoresSinGuardian.Keys
            .Select(k => k.Split('#')[0])
            .Where(ruta => !File.Exists(Path.Combine(raiz, ruta)))
            .Should().BeEmpty("el fichero se movió o se borró sin actualizar la lista");
    }

    // -------------------------------------------------------------------------------------------
    // Control del instrumento, con entradas sintéticas
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void El_analizador_distingue_contenedor_con_campos_con_y_sin_guardian()
    {
        var razor = """
            <Modal Titulo="A" VisibleChanged="X">
                <ChildContent><CampoTexto Etiqueta="Nombre" /></ChildContent>
            </Modal>
            <Drawer Titulo="B" HayCambios="() => HayCambiosSinGuardar">
                <ChildContent><CampoSelect Etiqueta="x"><option>1</option></CampoSelect></ChildContent>
            </Drawer>
            <Modal Titulo="C"><p>Solo lectura</p><CampoInfo Etiqueta="x" /></Modal>
            <Modal Titulo="D" Bloqueante="true"><input type="checkbox" /></Modal>
            <Modal Titulo="E" Bloqueante="_guardando"><input type="text" /></Modal>
            <Drawer Titulo="F" HayCambios="() => false"><CampoTexto Etiqueta="z" /></Drawer>
            <Drawer Titulo="G"><input type="hidden" value="1" /></Drawer>
            <Drawer Titulo="H"><SelectorClienteActivo /></Drawer>
            <Drawer Titulo="I" HayCambios="@(() => false)"><CampoTexto Etiqueta="z" /></Drawer>
            <Drawer Titulo="J" HayCambios="() => { return false; }"><CampoTexto Etiqueta="z" /></Drawer>
            <DialogoConfirmacion Titulo="K"><CampoTextarea Etiqueta="Motivo" /></DialogoConfirmacion>
            """;

        var medidos = Medir("x.razor", razor).ToDictionary(c => c.Clave);

        medidos["x.razor#Modal1"].Should().BeEquivalentTo(new { Campos = 1, TieneGuardian = false, Exento = false });
        medidos["x.razor#Drawer1"].Should().BeEquivalentTo(new { Campos = 1, TieneGuardian = true, Exento = false });
        medidos["x.razor#Modal2"].Campos.Should().Be(0, "CampoInfo es solo lectura");
        medidos["x.razor#Modal3"].Should().BeEquivalentTo(new { Campos = 1, TieneGuardian = false, Exento = true }, "Bloqueante fijo no se puede descartar");
        medidos["x.razor#Modal4"].Exento.Should().BeFalse("Bloqueante dinámico sí se puede cerrar fuera de ese instante");
        medidos["x.razor#Drawer2"].TieneGuardian.Should().BeFalse("() => false es un guardián que nunca pregunta");
        medidos["x.razor#Drawer3"].Campos.Should().Be(0, "un input hidden no es un campo");
        medidos["x.razor#Drawer4"].Campos.Should().Be(0, "SelectorClienteActivo actúa al instante, no es un campo");

        Evaluar(medidos.Values.ToList(), new Dictionary<string, string>())
            .Select(p => p.Split(':')[0])
            .Should().BeEquivalentTo(
                "x.razor#Modal1", "x.razor#Modal4", "x.razor#Drawer2", "x.razor#Drawer5", "x.razor#Drawer6", "x.razor#DialogoConfirmacion1");
    }

    /// <summary>
    /// D-05 (2026-10-02): «Cancelar» de un contenedor que pregunta al cerrar con la X pregunta igual. Control del instrumento con
    /// entradas sintéticas: cada caso es una forma real de escribir el botón en el repositorio.
    /// </summary>
    [Fact]
    public void El_analizador_exige_que_Cancelar_del_pie_pase_por_el_guardian_de_cierre()
    {
        var razor = """
            <Drawer @ref="_a" HayCambios="() => X" Visible="v"><ChildContent><CampoTexto Etiqueta="n" /></ChildContent>
                <Pie><Boton Variante="VarianteBoton.Secundario" OnClick="() => _vA = false">Cancelar</Boton></Pie></Drawer>
            <Drawer @ref="_b" HayCambios="() => X" Visible="v"><ChildContent><CampoTexto Etiqueta="n" /></ChildContent>
                <Pie><Boton Variante="VarianteBoton.Secundario" OnClick="() => _b?.SolicitarCierreAsync() ?? Task.CompletedTask">Cancelar</Boton></Pie></Drawer>
            <Modal @ref="_c" HayCambios="() => X" Visible="v"><ChildContent><CampoTexto Etiqueta="n" /></ChildContent>
                <Pie><Boton Variante="VarianteBoton.Secundario" OnClick="CancelarCAsync">@Textos["BotonCancelar"]</Boton></Pie></Modal>
            <Modal @ref="_d" HayCambios="() => X" Visible="v"><ChildContent><CampoTexto Etiqueta="n" /></ChildContent>
                <Pie><Boton Variante="VarianteBoton.Secundario" OnClick="CancelarDAsync">Cancelar</Boton></Pie></Modal>
            <Modal HayCambios="() => X" Visible="v"><ChildContent><CampoTexto Etiqueta="n" />
                <Boton Variante="VarianteBoton.Secundario" OnClick="() => _fila = null">Cancelar</Boton></ChildContent>
                <Pie><Boton Variante="VarianteBoton.Primario" OnClick="DescartarAsync">Descartar</Boton></Pie></Modal>
            <Modal Visible="v"><ChildContent><CampoTexto Etiqueta="n" /></ChildContent>
                <Pie><Boton Variante="VarianteBoton.Secundario" OnClick="() => _vF = false">Cancelar</Boton></Pie></Modal>
            <Drawer @ref="_g" HayCambios="() => X" Visible="v"><ChildContent><CampoTexto Etiqueta="n" /></ChildContent>
                <Pie><Boton Variante="VarianteBoton.Secundario" Deshabilitado="() => a" OnClick="CancelarGAsync">Cancelar</Boton></Pie></Drawer>
            <Drawer @ref="_h" HayCambios="() => X" Visible="v"><ChildContent><CampoTexto Etiqueta="n" /></ChildContent>
                <Pie><Boton Variante="VarianteBoton.Secundario" OnClick="() => CancelarHAsync()">Cancelar</Boton></Pie></Drawer>
            <Modal @ref="_i" HayCambios="() => X" Visible="v"><ChildContent><CampoTexto Etiqueta="n" /></ChildContent>
                <Pie><Boton Variante="VarianteBoton.Secundario" OnClick="() => _vI = false">@TextosAutorizar["Cancelar"]</Boton></Pie></Modal>
            <Drawer @ref="_j" HayCambios="() => X" Visible="v"><ChildContent><CampoTexto Etiqueta="n" />
                <SelectorLote OnCancelar="() => CerrarAsync(false)" /></ChildContent></Drawer>
            <Drawer @ref="_k" HayCambios="() => X" Visible="v"><ChildContent><CampoTexto Etiqueta="n" />
                <SelectorLote OnCancelar="() => _k?.SolicitarCierreAsync() ?? Task.CompletedTask" /></ChildContent></Drawer>
            <Drawer @ref="_l" HayCambios="() => X" Visible="v"><ChildContent><CampoTexto Etiqueta="n" /></ChildContent>
                <Pie><Boton Variante="@(esPrimaria ? VarianteBoton.Primario : VarianteBoton.Secundario)" OnClick="() => _vL = false">Cancelar</Boton></Pie></Drawer>
            """;

        var codigo = """
            private Task CancelarCAsync() => _c is { } modal ? modal.SolicitarCierreAsync() : CerrarAsync(false);
            private async Task CancelarDAsync() { _vD = false; await Task.CompletedTask; }
            private async Task CancelarGAsync()
            {
                if (_g is { } d) { await d.SolicitarCierreAsync(); }
            }
            private Task CancelarHAsync() => CerrarHAsync();
            private Task CerrarHAsync() => _h!.SolicitarCierreAsync();
            """;

        var medidos = Medir("x.razor", razor, codigo).ToDictionary(c => c.Clave);

        medidos["x.razor#Drawer1"].CancelarSinGuardian.Should().Equal("Cancelar");
        medidos["x.razor#Drawer2"].CancelarSinGuardian.Should().BeEmpty("la lambda llama a SolicitarCierreAsync del contenedor");
        medidos["x.razor#Modal1"].CancelarSinGuardian.Should().BeEmpty("el manejador del .razor.cs llama a SolicitarCierreAsync, aunque su cuerpo lleve llaves de un patrón «is { }»");
        medidos["x.razor#Modal2"].CancelarSinGuardian.Should().Equal("Cancelar");
        medidos["x.razor#Modal3"].CancelarSinGuardian.Should().BeEmpty("el Cancelar del cuerpo repliega un subformulario y el Descartar primario es la acción del diálogo");
        medidos["x.razor#Modal4"].CancelarSinGuardian.Should().Equal("Cancelar");
        medidos["x.razor#Drawer3"].CancelarSinGuardian.Should().BeEmpty("un « => » en un atributo anterior no esconde el OnClick, y el manejador de bloque llama al guardián");
        medidos["x.razor#Drawer4"].CancelarSinGuardian.Should().Equal("Cancelar");
        medidos["x.razor#Modal5"].CancelarSinGuardian.Should().Equal(["@TextosAutorizar[\"Cancelar\"]"], "el rótulo con otro localizador (TextosAutorizar) también es un Cancelar");
        medidos["x.razor#Drawer5"].CancelarSinGuardian.Should().Equal("OnCancelar de <SelectorLote>");
        medidos["x.razor#Drawer6"].CancelarSinGuardian.Should().BeEmpty("el OnCancelar del hijo está cableado al guardián");
        medidos["x.razor#Drawer7"].CancelarSinGuardian.Should().Equal("Cancelar"); // una variante condicional no exime al botón

        Evaluar(medidos.Values.ToList(), new Dictionary<string, string>())
            .Where(p => p.Contains("SolicitarCierreAsync", StringComparison.Ordinal))
            .Select(p => p.Split(':')[0])
            .Should().BeEquivalentTo("x.razor#Drawer1", "x.razor#Modal2", "x.razor#Drawer4", "x.razor#Modal5", "x.razor#Drawer5", "x.razor#Drawer7");
    }

    [Fact]
    public void Un_campo_se_atribuye_al_contenedor_mas_interior()
    {
        var razor = """
            <Drawer Titulo="Externo" HayCambios="() => X">
                <ChildContent>
                    <CampoTexto Etiqueta="a" />
                    <Modal Titulo="Interno"><CampoTexto Etiqueta="b" /></Modal>
                </ChildContent>
            </Drawer>
            """;

        var medidos = Medir("x.razor", razor).ToDictionary(c => c.Clave);

        medidos["x.razor#Drawer1"].Campos.Should().Be(1, "el campo del Modal interno no es del Drawer");
        medidos["x.razor#Modal1"].Campos.Should().Be(1);
        medidos["x.razor#Modal1"].TieneGuardian.Should().BeFalse("el guardián del Drawer externo no protege al Modal interno");
    }

    [Fact]
    public void El_trinquete_se_pone_rojo_por_el_motivo_previsto_y_detecta_excepciones_caducadas()
    {
        var sinGuardian = new Medido("a.razor#Modal1", 2, false, false);
        var lista = new Dictionary<string, string> { ["a.razor#Modal1"] = "motivo" };

        Evaluar([sinGuardian], new Dictionary<string, string>()).Should().ContainSingle(p => p.StartsWith("a.razor#Modal1:", StringComparison.Ordinal));
        Evaluar([sinGuardian], lista).Should().BeEmpty("la excepción lo cubre");
        Evaluar([sinGuardian with { TieneGuardian = true }], lista).Should().ContainSingle(p => p.Contains("retírala"), "ya protege");
        Evaluar([sinGuardian with { Campos = 0 }], lista).Should().ContainSingle(p => p.Contains("retírala"), "ya no tiene campos");
        Evaluar([], lista).Should().ContainSingle(p => p.Contains("retírala"), "el contenedor ya no existe");
    }

    // -------------------------------------------------------------------------------------------
    // Medición
    // -------------------------------------------------------------------------------------------

    /// <param name="CancelarSinGuardian">Rótulos de los «Cancelar»/«Descartar» del cuerpo del contenedor cuyo <c>OnClick</c> no llega a <c>SolicitarCierreAsync</c> (D-05).</param>
    private sealed record Medido(string Clave, int Campos, bool TieneGuardian, bool Exento, IReadOnlyList<string>? CancelarSinGuardian = null);

    private static List<string> Evaluar(IReadOnlyCollection<Medido> medidos, IReadOnlyDictionary<string, string> excepciones)
    {
        var problemas = new List<string>();

        foreach (var c in medidos.Where(c => c.Campos > 0 && !c.TieneGuardian && !c.Exento).OrderBy(c => c.Clave, StringComparer.Ordinal))
        {
            if (!excepciones.ContainsKey(c.Clave))
                problemas.Add($"{c.Clave}: {c.Campos} campo(s) y ningún HayCambios (ni Bloqueante fijo, ni excepción justificada)");
        }

        // D-05: si el contenedor pregunta al cerrar con la X, su «Cancelar» pregunta igual. Solo cuenta donde hay guardián real:
        // sin él no hay nada que preguntar, y los que no lo tienen ya los vigila el bloque anterior.
        foreach (var c in medidos.Where(c => c.TieneGuardian && !c.Exento).OrderBy(c => c.Clave, StringComparer.Ordinal))
        {
            foreach (var boton in c.CancelarSinGuardian ?? [])
                problemas.Add($"{c.Clave}: el botón «{boton}» cierra sin pasar por SolicitarCierreAsync del contenedor (D-05: «Cancelar» pregunta «¿Descartar cambios?» igual que la X)");
        }

        foreach (var clave in excepciones.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var c = medidos.FirstOrDefault(m => m.Clave == clave);
            if (c is null || c.Campos == 0 || c.TieneGuardian || c.Exento)
                problemas.Add($"{clave}: la excepción ya no hace falta (el contenedor no existe, no tiene campos, ya protege o es Bloqueante); retírala de la lista");
        }

        return problemas;
    }

    private static List<Medido> MedirTodos() =>
        MarcadoRazor.LeerRazorDeLaWeb().SelectMany(f => Medir(f.Ruta, f.Contenido, CodigoAsociado(f.Ruta))).ToList();

    /// <summary>El <c>.razor.cs</c> de al lado, si lo hay: allí viven muchos manejadores de «Cancelar».</summary>
    private static string CodigoAsociado(string ruta)
    {
        var fichero = Path.Combine(MarcadoRazor.RaizDelRepositorio(), ruta + ".cs");
        return File.Exists(fichero) ? File.ReadAllText(fichero) : string.Empty;
    }

    private static List<Medido> Medir(string ruta, string razor, string codigoAsociado = "")
    {
        var texto = LimpiadorDeComentarios.Quitar(razor, razor: true);
        var contenedores = MarcadoRazor.Elementos(texto, Contenedores).Where(e => !e.Autocerrado).ToList();

        // Cada campo, a su contenedor más interior (el de mayor inicio que lo contiene).
        var camposPorContenedor = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var campo in MarcadoRazor.Aperturas(texto, Campos).Where(a => !EsInputOculto(a)))
        {
            var dueno = contenedores
                .Where(c => c.Inicio < campo.Inicio && campo.Inicio < c.Fin)
                .OrderByDescending(c => c.Inicio)
                .FirstOrDefault();
            if (dueno is null) continue;
            var clave = $"{ruta}#{dueno.Nombre}{dueno.Ordinal}";
            camposPorContenedor[clave] = camposPorContenedor.GetValueOrDefault(clave) + 1;
        }

        // Cada «Cancelar» o «Descartar» que no usa el guardián, a su contenedor más interior.
        var codigo = texto + "\n" + codigoAsociado;
        var cancelaresPorContenedor = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // Solo los del pie del contenedor (<Pie>): el «Cancelar» de un subformulario dentro del cuerpo (una respuesta, una fila
        // en edición) repliega esa parte, no cierra el Drawer o Modal.
        var pies = MarcadoRazor.Elementos(texto, "Pie").Where(e => !e.Autocerrado).ToList();
        foreach (var boton in MarcadoRazor.Elementos(texto, "Boton|button").Where(b => !b.Autocerrado && EsBotonDeCancelar(b)))
        {
            if (UsaElGuardianDeCierre(boton.Apertura, codigo)) continue;
            var dueno = contenedores
                .Where(c => c.Inicio < boton.Inicio && boton.Inicio < c.Fin)
                .OrderByDescending(c => c.Inicio)
                .FirstOrDefault();
            if (dueno is null) continue;
            if (!pies.Any(p => p.Inicio > dueno.Inicio && p.Inicio < boton.Inicio && boton.Inicio < p.Fin)) continue;
            var clave = $"{ruta}#{dueno.Nombre}{dueno.Ordinal}";
            if (!cancelaresPorContenedor.TryGetValue(clave, out var lista))
                cancelaresPorContenedor[clave] = lista = [];
            lista.Add(TextoVisible(boton.Cuerpo));
        }

        // El «Cancelar» que vive en un componente hijo (SelectorLoteDocumental) llega al padre por OnCancelar: el padre lo cablea al
        // guardián del contenedor en vez de cerrar directo. Sin la restricción del <Pie>: el hijo pinta su propio pie.
        foreach (var hijo in MarcadoRazor.Aperturas(texto, @"[A-Z]\w*").Where(a => AtributoOnCancelar.IsMatch(a.Texto)))
        {
            if (UsaElGuardianDeCierre(hijo.Texto, codigo, AtributoOnCancelar)) continue;
            var dueno = contenedores
                .Where(c => c.Inicio < hijo.Inicio && hijo.Inicio < c.Fin)
                .OrderByDescending(c => c.Inicio)
                .FirstOrDefault();
            if (dueno is null) continue;
            var clave = $"{ruta}#{dueno.Nombre}{dueno.Ordinal}";
            if (!cancelaresPorContenedor.TryGetValue(clave, out var lista))
                cancelaresPorContenedor[clave] = lista = [];
            lista.Add($"OnCancelar de <{hijo.Nombre}>");
        }

        return contenedores.Select(c =>
        {
            var clave = $"{ruta}#{c.Nombre}{c.Ordinal}";
            return new Medido(
                clave, camposPorContenedor.GetValueOrDefault(clave), GuardianReal(c.Apertura), BloqueanteFijo.IsMatch(c.Apertura),
                cancelaresPorContenedor.GetValueOrDefault(clave) ?? []);
        }).ToList();
    }

    // «Cancelar» o «Descartar» (y «Descartar cambios»), tal cual o como @Textos["…Cancelar…"]. Un botón con otro rótulo («Volver»,
    // «Cerrar») no es de este contrato: ese lo decide el diálogo, no el formulario.
    private static readonly Regex RotuloDeCancelar = new(
        @"^(?:Cancelar|Descartar(?:\s+cambios)?|@\w*Textos\w*\[""[^""]*Cancelar[^""]*""\])$", RegexOptions.Compiled);

    private static readonly Regex AtributoOnClick = new(@"(?:^|\s)OnClick\s*=\s*""", RegexOptions.Compiled);

    private static readonly Regex AtributoOnCancelar = new(@"(?:^|\s)OnCancelar\s*=\s*""", RegexOptions.Compiled);

    private static string TextoVisible(string cuerpo) =>
        Regex.Replace(Regex.Replace(cuerpo, @"<[^>]*>", " "), @"\s+", " ").Trim();

    // El botón que cierra sin hacer nada nunca es el primario ni el destructivo: un «Descartar» primario (Retencion.razor, «Descartar»
    // la retención programada) es la acción del diálogo, no su salida.
    private static readonly Regex VarianteDeAccion = new(@"Variante\s*=\s*""VarianteBoton\.(?:Primario|Destructivo)""", RegexOptions.Compiled);

    private static bool EsBotonDeCancelar(MarcadoRazor.Elemento boton) =>
        RotuloDeCancelar.IsMatch(TextoVisible(boton.Cuerpo)) && !VarianteDeAccion.IsMatch(boton.Apertura);

    /// <summary>
    /// El <c>OnClick</c> llama a <c>SolicitarCierreAsync</c> del contenedor, directamente (lambda) o a través de un manejador del
    /// mismo componente (<c>.razor</c> o <c>.razor.cs</c>) cuyo cuerpo lo llama. Un solo salto: el manejador que delega en otro
    /// manejador no cuenta (que el guardián se vea en el manejador del botón es parte del contrato).
    /// </summary>
    private static bool UsaElGuardianDeCierre(string apertura, string codigo, Regex? atributo = null)
    {
        var m = (atributo ?? AtributoOnClick).Match(apertura);
        if (!m.Success) return false;
        var valor = MarcadoRazor.ValorDeComillas(apertura, m.Index + m.Length);
        if (valor.Contains("SolicitarCierreAsync", StringComparison.Ordinal)) return true;

        return Regex.Matches(valor, @"(?<![\w.])([A-Za-z_]\w*)\b")
            .Select(i => i.Groups[1].Value)
            .Distinct()
            .Any(nombre => CuerposDeMetodo(codigo, nombre).Any(cuerpo => cuerpo.Contains("SolicitarCierreAsync", StringComparison.Ordinal)));
    }

    /// <summary>Los cuerpos (de bloque o de expresión) de los métodos <c>Task/void/ValueTask nombre(...)</c> del código.</summary>
    private static IEnumerable<string> CuerposDeMetodo(string codigo, string nombre)
    {
        var declaracion = new Regex($@"\b(?:Task|ValueTask|void)(?:<[^>()]*>)?\s+{Regex.Escape(nombre)}\s*\(");
        foreach (Match d in declaracion.Matches(codigo))
        {
            var j = d.Index + d.Length;
            var parentesis = 1;
            while (j < codigo.Length && parentesis > 0)
            {
                if (codigo[j] == '(') parentesis++;
                else if (codigo[j] == ')') parentesis--;
                j++;
            }

            // Cuerpo de expresión («=> …;», que puede llevar llaves de un patrón «is { }») o de bloque («{ … }»).
            while (j < codigo.Length && char.IsWhiteSpace(codigo[j])) j++;
            var deExpresion = string.CompareOrdinal(codigo, j, "=>", 0, 2) == 0;
            var inicio = j;
            var llaves = 0;
            for (; j < codigo.Length; j++)
            {
                if (codigo[j] == '{') llaves++;
                else if (codigo[j] == '}') { llaves--; if (llaves == 0 && !deExpresion) { j++; break; } }
                else if (codigo[j] == ';' && llaves == 0) { j++; break; }
            }

            yield return codigo[inicio..Math.Min(j, codigo.Length)];
        }
    }

    private static bool EsInputOculto(MarcadoRazor.Apertura a) =>
        a.Nombre == "input" && Regex.IsMatch(a.Texto, @"\btype\s*=\s*""hidden""");

    /// <summary>Pasa <c>HayCambios</c> y no es el literal que nunca pregunta.</summary>
    private static bool GuardianReal(string apertura)
    {
        var m = AtributoHayCambios.Match(apertura);
        if (!m.Success) return false;
        var valor = Regex.Replace(MarcadoRazor.ValorDeComillas(apertura, m.Index + m.Length), @"\s+", string.Empty).TrimStart('@');
        return !GuardianesInertes.Contains(valor);
    }
}
