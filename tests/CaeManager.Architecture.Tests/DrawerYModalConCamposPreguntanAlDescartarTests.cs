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
/// <b>todo <c>&lt;Drawer&gt;</c> o <c>&lt;Modal&gt;</c> cuyo cuerpo contiene campos pasa <c>HayCambios</c></b>,
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
/// </summary>
public class DrawerYModalConCamposPreguntanAlDescartarTests
{
    private const string Contenedores = "Drawer|Modal";

    private const string Campos =
        "CampoTexto|CampoSelect|CampoTextarea|CampoBuscarSelect|SelectorEntidad|SelectorMultiple|ZonaSoltarArchivo|" +
        "InputText|InputTextArea|InputNumber|InputSelect|InputDate|InputCheckbox|InputFile|textarea|select|input";

    private static readonly Regex AtributoHayCambios = new(@"(?:^|\s)HayCambios\s*=\s*""", RegexOptions.Compiled);

    // «Bloqueante» a secas o ="true": no hay X, ni Escape, ni cierre por el fondo, así que no hay nada que preguntar.
    // Bloqueante="_guardando" (dinámico) no vale: fuera de ese instante, el contenedor se puede cerrar.
    private static readonly Regex BloqueanteFijo = new(@"(?:^|\s)Bloqueante(?:\s*=\s*""(?:true|True)"")?(?=[\s/>])", RegexOptions.Compiled);

    private static readonly Regex GuardianQueNuncaPregunta = new(@"^\s*(?:\(\s*\)\s*=>\s*)?false\s*$", RegexOptions.Compiled);

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
    };

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
            .Should().BeEquivalentTo("x.razor#Modal1", "x.razor#Modal4", "x.razor#Drawer2");
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

    private sealed record Medido(string Clave, int Campos, bool TieneGuardian, bool Exento);

    private static List<string> Evaluar(IReadOnlyCollection<Medido> medidos, IReadOnlyDictionary<string, string> excepciones)
    {
        var problemas = new List<string>();

        foreach (var c in medidos.Where(c => c.Campos > 0 && !c.TieneGuardian && !c.Exento).OrderBy(c => c.Clave, StringComparer.Ordinal))
        {
            if (!excepciones.ContainsKey(c.Clave))
                problemas.Add($"{c.Clave}: {c.Campos} campo(s) y ningún HayCambios (ni Bloqueante fijo, ni excepción justificada)");
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
        MarcadoRazor.LeerRazorDeLaWeb().SelectMany(f => Medir(f.Ruta, f.Contenido)).ToList();

    private static List<Medido> Medir(string ruta, string razor)
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

        return contenedores.Select(c =>
        {
            var clave = $"{ruta}#{c.Nombre}{c.Ordinal}";
            return new Medido(clave, camposPorContenedor.GetValueOrDefault(clave), GuardianReal(c.Apertura), BloqueanteFijo.IsMatch(c.Apertura));
        }).ToList();
    }

    private static bool EsInputOculto(MarcadoRazor.Apertura a) =>
        a.Nombre == "input" && Regex.IsMatch(a.Texto, @"\btype\s*=\s*""hidden""");

    /// <summary>Pasa <c>HayCambios</c> y no es el literal que nunca pregunta.</summary>
    private static bool GuardianReal(string apertura)
    {
        var m = AtributoHayCambios.Match(apertura);
        return m.Success && !GuardianQueNuncaPregunta.IsMatch(MarcadoRazor.ValorDeComillas(apertura, m.Index + m.Length));
    }
}
