using System.Text.RegularExpressions;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// S2a (fase 0 de salvaguardas, 2026-10-02). Dos propiedades sobre <c>&lt;Boton&gt;</c>:
///
/// <list type="number">
/// <item><b>La variante se declara.</b> Antes valía <c>Primario</c> por defecto y 264 de 600 etiquetas
/// eran primarias sin haberlo decidido (D-30: dos primarios en una misma vista). Ahora
/// <c>Boton.Variante</c> es <c>[EditorRequired]</c> (RZ2012, que <c>-warnaserror</c> convierte en error en
/// el build de CI) y este test lo repite sobre el fuente, porque un build sin <c>-warnaserror</c> (la imagen
/// Docker, un build local) solo avisaría.</item>
/// <item><b>Un solo primario por superficie</b> (decisión del propietario, 2026-10-02). Trinquete con lista de
/// excepciones por ubicación: una superficie nueva con más de un primario falla siempre, y una excepción ya
/// listada no puede ni subir ni quedarse holgada (si baja, hay que bajar su número aquí).</item>
/// </list>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> «Superficie» es: (a) el cuerpo de un <c>.razor</c> fuera
/// de todo <c>Drawer</c>, <c>Modal</c> o <c>DialogoConfirmacion</c> (la «vista»), y (b) cada uno de esos
/// contenedores, numerado por orden de aparición en su fichero (<c>#Drawer1</c>). Un primario es un
/// <c>Variante</c> cuyo valor contiene <c>VarianteBoton.Primario</c>, también dentro de un condicional
/// (<c>@(x ? Primario : Secundario)</c> cuenta). NO ve: una pantalla compuesta de varios componentes hijos
/// con un primario cada uno (Centro 360 pinta «Programar visita» en <c>CentroDetalle</c> y «+ Asignar
/// trabajador» en <c>AcordeonAsignacionesCentro</c>, dos primarios que el mockup quiere y que este test no
/// puede sumar); una <c>Variante="Variante"</c> o <c>VarianteConfirmar</c> que llegue por parámetro (su valor
/// real lo pone el llamador, que sí se cuenta en su fichero); botones con primarios en ramas excluyentes
/// (<c>@if/else</c>), que cuentan como si convivieran. Esas ramas excluyentes son parte de la lista, no un
/// agujero: se retiran de ella al revisar cada pantalla.
/// </para>
/// </summary>
public class BotonVarianteYUnSoloPrimarioTests
{
    private static readonly Regex AperturaBoton = new(@"<Boton(?=[\s>/])", RegexOptions.Compiled);
    private static readonly Regex AperturaContenedor = new(@"<(Drawer|Modal|DialogoConfirmacion)(?=[\s>/])", RegexOptions.Compiled);
    private static readonly Regex AtributoVariante = new(@"(?:^|\s)Variante\s*=\s*""", RegexOptions.Compiled);

    /// <summary>
    /// Superficies que hoy tienen más de un primario, con cuántos. Es una lista que solo puede bajar:
    /// cada pasada de D-30 por una pantalla la retira (o baja su número) en el mismo cambio. Nació de
    /// convertir el primario implícito de 264 etiquetas en explícito sin cambiar su aspecto (2026-10-02).
    /// La clave es la ruta desde la raíz del repositorio, más <c>#Drawer1</c>, <c>#Modal2</c>… para un
    /// contenedor.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> PrimariosPermitidosPorSuperficie = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/CaeManager.Web/Components/Workspace/PestanaAgendaContactos.razor"] = 2,
        ["src/CaeManager.Web/Components/Workspace/PestanaDocumentacion.razor"] = 2,
        ["src/CaeManager.Web/Features/Alertas/Pages/Alertas.razor"] = 3,
        ["src/CaeManager.Web/Features/Bandeja/Pages/Bandeja.razor"] = 2,
        ["src/CaeManager.Web/Features/Bandeja/Pages/MiTrabajo.razor"] = 3,
        ["src/CaeManager.Web/Features/Blindaje42/Components/DrawerBlindaje42.razor#Drawer1"] = 3,
        ["src/CaeManager.Web/Features/Centros/Components/AcordeonAsignacionesCentro.razor"] = 3,
        ["src/CaeManager.Web/Features/Centros/Components/CentroWorkspacePanel.razor"] = 6,
        ["src/CaeManager.Web/Features/Centros/Pages/CentroDetalle.razor"] = 2,
        ["src/CaeManager.Web/Features/Centros/Pages/Centros.razor"] = 3,
        ["src/CaeManager.Web/Features/Clientes/Components/ClienteWorkspacePanel.razor"] = 6,
        ["src/CaeManager.Web/Features/Clientes/Pages/AltaGuiada.razor"] = 6,
        ["src/CaeManager.Web/Features/Clientes/Pages/ClienteDetalle.razor"] = 4,
        ["src/CaeManager.Web/Features/Clientes/Pages/Clientes.razor"] = 3,
        ["src/CaeManager.Web/Features/Clientes/Pages/ConfiguracionIaCliente.razor"] = 2,
        ["src/CaeManager.Web/Features/Comunicaciones/Components/AccionCenter.razor"] = 3,
        ["src/CaeManager.Web/Features/Comunicaciones/Components/ComposerBar.razor"] = 3,
        ["src/CaeManager.Web/Features/Comunicaciones/Components/RevisionSugerenciaModal.razor#Modal1"] = 2,
        ["src/CaeManager.Web/Features/Comunicaciones/Pages/Bandeja.razor"] = 4,
        ["src/CaeManager.Web/Features/Comunicaciones/Pages/Buzon.razor"] = 3,
        ["src/CaeManager.Web/Features/Comunicaciones/Pages/Macros.razor"] = 4,
        ["src/CaeManager.Web/Features/Comunicaciones/Pages/Macros.razor#Drawer1"] = 2,
        ["src/CaeManager.Web/Features/Configuracion/Components/ParametrosSistemaPanel.razor"] = 4,
        ["src/CaeManager.Web/Features/DashboardEjecutivo/Pages/DashboardEjecutivo.razor"] = 2,
        ["src/CaeManager.Web/Features/Delegaciones/Pages/Delegaciones.razor"] = 3,
        ["src/CaeManager.Web/Features/Documentos/Components/DocumentoWorkspacePanel.razor"] = 2,
        ["src/CaeManager.Web/Features/Documentos/Components/DrawerGestionDocumento.razor#Drawer1"] = 2,
        ["src/CaeManager.Web/Features/Documentos/Components/FirmaEnCampoTab.razor"] = 4,
        ["src/CaeManager.Web/Features/Documentos/Components/PlantillasTab.razor"] = 2,
        ["src/CaeManager.Web/Features/Documentos/Components/PlataformaTab.razor"] = 2,
        ["src/CaeManager.Web/Features/Documentos/Components/ReclamacionesTab.razor"] = 3,
        ["src/CaeManager.Web/Features/Documentos/Components/RevisionIaTab.razor"] = 2,
        ["src/CaeManager.Web/Features/Documentos/Components/SugerenciasPreventivasTab.razor"] = 2,
        ["src/CaeManager.Web/Features/Documentos/Pages/Documentos.razor"] = 3,
        ["src/CaeManager.Web/Features/Documentos/Pages/SubidaMasiva.razor"] = 2,
        ["src/CaeManager.Web/Features/Empresas/Components/EmpresaWorkspacePanel.razor"] = 3,
        ["src/CaeManager.Web/Features/Empresas/Pages/DeteccionTrabajadores.razor"] = 2,
        ["src/CaeManager.Web/Features/Empresas/Pages/EmpresaDetalle.razor"] = 4,
        ["src/CaeManager.Web/Features/Empresas/Pages/Empresas.razor"] = 3,
        ["src/CaeManager.Web/Features/Facturacion/Pages/Facturacion.razor"] = 4,
        ["src/CaeManager.Web/Features/GestionRoles/Pages/Roles.razor"] = 2,
        ["src/CaeManager.Web/Features/Gestiones/Pages/Gestiones.razor"] = 2,
        ["src/CaeManager.Web/Features/Importacion/Pages/Importacion.razor"] = 5,
        ["src/CaeManager.Web/Features/Incidencias/Pages/Incidencias.razor"] = 3,
        ["src/CaeManager.Web/Features/IncorporacionCartera/Pages/SolicitudesCartera.razor"] = 4,
        ["src/CaeManager.Web/Features/Plantillas/Components/DocumentosGeneradosPanel.razor"] = 2,
        ["src/CaeManager.Web/Features/Plantillas/Pages/ConfigurarPlantilla.razor"] = 5,
        ["src/CaeManager.Web/Features/Plataforma/Pages/Plataforma.razor"] = 2,
        ["src/CaeManager.Web/Features/Proyectos/Pages/Proyectos.razor"] = 6,
        ["src/CaeManager.Web/Features/Reportes/Pages/Reportes.razor"] = 2,
        ["src/CaeManager.Web/Features/Retencion/Pages/Retencion.razor"] = 2,
        ["src/CaeManager.Web/Features/Subcontratas/Components/SubcontrataPreviewDrawer.razor"] = 2,
        ["src/CaeManager.Web/Features/Subcontratas/Components/SubcontrataWorkspacePanel.razor"] = 6,
        ["src/CaeManager.Web/Features/Subcontratas/Pages/Subcontratas.razor"] = 3,
        ["src/CaeManager.Web/Features/TiposDocumento/Pages/TiposDocumento.razor"] = 3,
        ["src/CaeManager.Web/Features/Trabajadores/Components/TrabajadorWorkspacePanel.razor"] = 3,
        ["src/CaeManager.Web/Features/Trabajadores/Pages/TrabajadorDetalle.razor"] = 4,
        ["src/CaeManager.Web/Features/Trabajadores/Pages/Trabajadores.razor"] = 3,
        ["src/CaeManager.Web/Features/Usuarios/Pages/MiFirma.razor"] = 3,
        ["src/CaeManager.Web/Features/Usuarios/Pages/Usuarios.razor"] = 2,
        ["src/CaeManager.Web/Features/Vehiculos/Components/VehiculoWorkspacePanel.razor"] = 2,
        ["src/CaeManager.Web/Features/Vehiculos/Pages/Vehiculos.razor"] = 2,
        ["src/CaeManager.Web/Features/Visitas/Pages/Visitas.razor"] = 2,
        ["src/CaeManager.Web/Features/Visitas/Pages/Visitas.razor#Drawer2"] = 3,
    };

    // -------------------------------------------------------------------------------------------
    // 1. La variante se declara
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Boton_Variante_es_obligatoria_para_el_compilador()
    {
        var propiedad = typeof(Boton).GetProperty(nameof(Boton.Variante));

        propiedad.Should().NotBeNull();
        propiedad!.GetCustomAttributes(typeof(EditorRequiredAttribute), inherit: true).Should().NotBeEmpty(
            "sin [EditorRequired], un <Boton> sin Variante vuelve a compilar en silencio como Primario");
    }

    [Fact]
    public void Todo_Boton_del_fuente_declara_su_Variante()
    {
        var sin = new List<string>();
        var etiquetas = 0;

        foreach (var (ruta, texto) in LeerRazor())
        {
            foreach (var boton in Analizar(texto).Botones)
            {
                etiquetas++;
                if (!boton.DeclaraVariante) sin.Add(ruta);
            }
        }

        // Control positivo: si el recorrido no viera las etiquetas, «ninguna sin variante» valdría por vacío.
        etiquetas.Should().BeGreaterThan(500, "al escribirlo había 600 <Boton> en 113 ficheros; si baja de golpe, dejó de mirar");

        string.Join(Environment.NewLine, sin.GroupBy(x => x).Select(g => $"{g.Key} ({g.Count()})").OrderBy(x => x)).Should().BeEmpty(
            "cada <Boton> declara Variante (Primario, Secundario, Fantasma o Destructivo); un solo Primario por vista");
    }

    // -------------------------------------------------------------------------------------------
    // 2. Un solo primario por superficie
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Una_superficie_con_mas_de_un_primario_esta_en_la_lista_con_su_numero_exacto()
    {
        var medido = MedirPrimariosPorSuperficie();

        // Control positivo: la medición ve superficies reales y la lista no está vacía por accidente.
        medido.Values.Sum().Should().BeGreaterThan(80, "hay ≈100 primarios declarados al escribirlo; si baja de golpe, dejó de mirar");

        var problemas = Evaluar(medido, PrimariosPermitidosPorSuperficie);

        string.Join(Environment.NewLine, problemas).Should().BeEmpty(
            "un solo primario por superficie. Un primario de más se resuelve bajándolo a Secundario/Fantasma, no subiendo la lista; " +
            "si bajaste uno, baja su número en PrimariosPermitidosPorSuperficie (la lista solo puede decrecer)");
    }

    [Fact]
    public void Las_superficies_de_la_lista_existen_y_son_realmente_excepciones()
    {
        var raiz = RaizDelRepositorio();
        var inexistentes = PrimariosPermitidosPorSuperficie.Keys
            .Select(k => k.Split('#')[0])
            .Where(ruta => !File.Exists(Path.Combine(raiz, ruta)))
            .ToList();

        inexistentes.Should().BeEmpty("el fichero se movió o se borró sin actualizar la lista");
        PrimariosPermitidosPorSuperficie.Values.Should().OnlyContain(n => n > 1, "una excepción permite más de uno; con uno o menos sobra");
    }

    // -------------------------------------------------------------------------------------------
    // Control del instrumento, con entradas sintéticas
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void El_analizador_ve_la_variante_ausente_y_los_primarios_y_no_se_confunde_con_lambdas()
    {
        var razor = """
            <Boton OnClick="() => Abrir("a>b")">Sin variante</Boton>
            <Boton Variante="VarianteBoton.Primario" OnClick="X">Uno</Boton>
            <Boton
                Variante="@(esPrimaria ? VarianteBoton.Primario : VarianteBoton.Secundario)"
                OnClick="Y">Dos</Boton>
            <Boton Variante="VarianteBoton.Secundario">Tres</Boton>
            <BotonCopiar Valor="x" />
            <Boton Variante="VarianteConfirmar" />
            """;

        var resultado = Analizar(razor);

        resultado.Botones.Should().HaveCount(5, "BotonCopiar no es Boton");
        resultado.Botones.Count(b => !b.DeclaraVariante).Should().Be(1);
        resultado.Botones.Count(b => b.EsPrimario).Should().Be(2, "el condicional con Primario cuenta; el parámetro opaco no");
    }

    [Fact]
    public void Los_botones_de_un_Drawer_o_Modal_cuentan_en_su_contenedor_y_no_en_la_vista()
    {
        var razor = """
            <Boton Variante="VarianteBoton.Primario">Vista</Boton>
            <Drawer Titulo="A">
                <ChildContent><Boton Variante="VarianteBoton.Primario">Uno</Boton></ChildContent>
                <Pie><Boton Variante="VarianteBoton.Primario">Dos</Boton></Pie>
            </Drawer>
            <Modal Titulo="B"><Boton Variante="VarianteBoton.Secundario">Tres</Boton></Modal>
            <Drawer Titulo="C" />
            <Drawer Titulo="D"><Boton Variante="VarianteBoton.Primario">Cuatro</Boton></Drawer>
            """;

        var medido = Medir("x.razor", razor);

        medido.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["x.razor"] = 1,
            ["x.razor#Drawer1"] = 2,
            ["x.razor#Drawer3"] = 1,
        });
    }

    [Fact]
    public void El_trinquete_se_pone_rojo_por_el_motivo_previsto()
    {
        var lista = new Dictionary<string, int> { ["a.razor"] = 3 };

        Evaluar(new Dictionary<string, int> { ["a.razor"] = 3 }, lista).Should().BeEmpty();
        Evaluar(new Dictionary<string, int> { ["a.razor"] = 4 }, lista).Should().ContainSingle(m => m.Contains("a.razor") && m.Contains("sube"));
        Evaluar(new Dictionary<string, int> { ["a.razor"] = 2 }, lista).Should().ContainSingle(m => m.Contains("a.razor") && m.Contains("baja"));
        Evaluar(new Dictionary<string, int> { ["a.razor"] = 1 }, lista).Should().ContainSingle(m => m.Contains("a.razor") && m.Contains("baja"));
        Evaluar(new Dictionary<string, int> { ["b.razor"] = 2, ["a.razor"] = 3 }, lista).Should().ContainSingle(m => m.Contains("b.razor") && m.Contains("sin excepción"));
        Evaluar(new Dictionary<string, int> { ["b.razor"] = 1, ["a.razor"] = 3 }, lista).Should().BeEmpty("un solo primario es lo normal");
    }

    // -------------------------------------------------------------------------------------------
    // Medición
    // -------------------------------------------------------------------------------------------

    private static List<string> Evaluar(IReadOnlyDictionary<string, int> medido, IReadOnlyDictionary<string, int> permitido)
    {
        var problemas = new List<string>();

        foreach (var (superficie, n) in medido.Where(m => m.Value > 1).OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            if (!permitido.TryGetValue(superficie, out var tope))
                problemas.Add($"{superficie}: {n} primarios y sin excepción en la lista (entrada: [\"{superficie}\"] = {n},)");
            else if (n > tope)
                problemas.Add($"{superficie}: sube de {tope} a {n} primarios");
            else if (n < tope)
                problemas.Add($"{superficie}: baja de {tope} a {n} primarios; baja su número en la lista (entrada: [\"{superficie}\"] = {n},)");
        }

        foreach (var (superficie, tope) in permitido.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            var n = medido.TryGetValue(superficie, out var real) ? real : 0;
            if (n <= 1)
                problemas.Add($"{superficie}: baja de {tope} a {n} primarios; retírala de la lista");
        }

        return problemas;
    }

    private static Dictionary<string, int> MedirPrimariosPorSuperficie()
    {
        var medido = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (ruta, texto) in LeerRazor())
            foreach (var (superficie, n) in Medir(ruta, texto))
                medido[superficie] = n;
        return medido;
    }

    private static Dictionary<string, int> Medir(string ruta, string razor)
    {
        var analisis = Analizar(razor);
        var porSuperficie = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var boton in analisis.Botones.Where(b => b.EsPrimario))
        {
            // El contenedor más interior que contiene el botón; ninguno = la vista.
            var contenedor = analisis.Contenedores
                .Where(c => c.Inicio <= boton.Inicio && boton.Inicio < c.Fin)
                .OrderByDescending(c => c.Inicio)
                .FirstOrDefault();
            var clave = contenedor is null ? ruta : $"{ruta}#{contenedor.Nombre}";
            porSuperficie[clave] = porSuperficie.GetValueOrDefault(clave) + 1;
        }

        return porSuperficie;
    }

    private sealed record BotonAnalizado(int Inicio, bool DeclaraVariante, bool EsPrimario);

    private sealed record ContenedorAnalizado(string Nombre, int Inicio, int Fin);

    private sealed record Analisis(List<BotonAnalizado> Botones, List<ContenedorAnalizado> Contenedores);

    private static Analisis Analizar(string razor)
    {
        var texto = LimpiadorDeComentarios.Quitar(razor, razor: true);
        var botones = new List<BotonAnalizado>();
        var contenedores = new List<ContenedorAnalizado>();

        foreach (Match m in AperturaBoton.Matches(texto))
        {
            var fin = FinDeEtiqueta(texto, m.Index + m.Length);
            var cuerpo = texto[(m.Index + m.Length)..fin];
            var declara = AtributoVariante.Match(cuerpo);
            var valor = declara.Success ? ValorDeComillas(cuerpo, declara.Index + declara.Length) : string.Empty;
            botones.Add(new BotonAnalizado(m.Index, declara.Success, valor.Contains("VarianteBoton.Primario", StringComparison.Ordinal)));
        }

        var ordinales = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in AperturaContenedor.Matches(texto))
        {
            var nombre = m.Groups[1].Value;
            ordinales[nombre] = ordinales.GetValueOrDefault(nombre) + 1;
            var finApertura = FinDeEtiqueta(texto, m.Index + m.Length);
            if (texto[finApertura - 1] == '/') continue;

            contenedores.Add(new ContenedorAnalizado($"{nombre}{ordinales[nombre]}", m.Index, CierreDe(texto, nombre, finApertura + 1)));
        }

        return new Analisis(botones, contenedores);
    }

    /// <summary>Posición del <c>&gt;</c> que cierra una etiqueta de apertura; respeta comillas y expresiones con paréntesis.</summary>
    private static int FinDeEtiqueta(string s, int desde)
    {
        var j = desde;
        while (j < s.Length)
        {
            if (s[j] == '"')
                j = FinDeComillas(s, j + 1) + 1;
            else if (s[j] == '>')
                return j;
            else
                j++;
        }

        return s.Length - 1;
    }

    /// <summary>Posición de la comilla que cierra un valor de atributo que empieza en <paramref name="desde"/> (tras la comilla de apertura).</summary>
    private static int FinDeComillas(string s, int desde)
    {
        var j = desde;
        var profundidad = 0;
        while (j < s.Length)
        {
            var c = s[j];
            if (profundidad == 0 && c == '"') return j;
            if (c is '(' or '{' or '[') profundidad++;
            else if (c is ')' or '}' or ']') profundidad--;
            else if (c == '"' && profundidad > 0)
            {
                // Cadena dentro de una expresión: se salta entera, con sus escapes.
                j++;
                while (j < s.Length && s[j] != '"')
                {
                    if (s[j] == '\\') j++;
                    j++;
                }
            }

            j++;
        }

        return s.Length - 1;
    }

    private static string ValorDeComillas(string s, int desde) => s[desde..FinDeComillas(s, desde)];

    /// <summary>Fin del elemento <c>&lt;Nombre&gt;…&lt;/Nombre&gt;</c> que se abrió justo antes de <paramref name="desde"/>, con anidamiento del mismo nombre.</summary>
    private static int CierreDe(string s, string nombre, int desde)
    {
        var patron = new Regex($@"<{nombre}(?=[\s>/])|</{nombre}>");
        var profundidad = 1;
        var j = desde;
        while (profundidad > 0)
        {
            var m = patron.Match(s, j);
            if (!m.Success) return s.Length;
            if (m.Value.StartsWith("</", StringComparison.Ordinal)) profundidad--;
            else if (s[FinDeEtiqueta(s, m.Index + m.Length) - 1] != '/') profundidad++;
            j = m.Index + m.Length;
        }

        return j;
    }

    private static IEnumerable<(string Ruta, string Contenido)> LeerRazor()
    {
        var raiz = RaizDelRepositorio();
        var web = Path.Combine(raiz, "src", "CaeManager.Web");
        var sep = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Ruta: Path.GetRelativePath(raiz, f).Replace('\\', '/'), Contenido: File.ReadAllText(f)));
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
