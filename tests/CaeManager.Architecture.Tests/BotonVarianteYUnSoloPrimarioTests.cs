using System.Text.RegularExpressions;
using CaeManager.Architecture.Tests.Soporte;
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
/// agujero: se retiran de ella al revisar cada pantalla. Tampoco ve el primario que pinte un componente
/// envoltorio por dentro: cuenta una vez en su fichero, no en cada uso. Un primario que no pase por
/// <c>&lt;Boton&gt;</c> (marcado a pelo con <c>class="boton-primario"</c>, como tenían
/// <c>ConfigurarAutenticadorDosFactores</c> y <c>OrdenMenuLateral</c>) ya no se cuenta de otra forma:
/// <see cref="Ningun_primario_se_marca_a_pelo_fuera_de_Boton"/> lo prohíbe, de modo que todo primario del
/// fuente es un <c>&lt;Boton Variante="…Primario"&gt;</c> y entra en la medición de arriba.
/// </para>
/// </summary>
public class BotonVarianteYUnSoloPrimarioTests
{
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
        ["src/CaeManager.Web/Components/Account/Pages/ConfigurarAutenticadorDosFactores.razor"] = 4,
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
        ["src/CaeManager.Web/Features/Configuracion/Pages/OrdenMenuLateral.razor"] = 2,
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
        etiquetas.Should().BeGreaterThan(550, "al escribirlo había 600 <Boton> en 113 ficheros; si baja de golpe, dejó de mirar");

        string.Join(Environment.NewLine, sin.GroupBy(x => x).Select(g => $"{g.Key} ({g.Count()})").OrderBy(x => x)).Should().BeEmpty(
            "cada <Boton> declara Variante (Primario, Secundario, Fantasma o Destructivo); un solo Primario por vista");
    }

    /// <summary>
    /// Una clase de primario («boton-primario», «orden-menu-boton-primario»…) puesta a mano en un <c>&lt;a&gt;</c> o
    /// <c>&lt;button&gt;</c> es un primario que el conteo de abajo no ve (S2a lo dejó fuera: 6 de ellos en 2 ficheros). El único
    /// sitio que escribe «boton-primario» es la hoja de <c>Boton</c>, que lo compone en <c>Boton.razor</c> a partir de la
    /// variante; en los demás <c>.razor</c>, un primario es <c>&lt;Boton Variante="VarianteBoton.Primario"&gt;</c>.
    /// </summary>
    [Fact]
    public void Ningun_primario_se_marca_a_pelo_fuera_de_Boton()
    {
        var infractores = new List<string>();
        var ficheros = 0;

        foreach (var (ruta, texto) in LeerRazor())
        {
            ficheros++;
            var limpio = LimpiadorDeComentarios.Quitar(texto, razor: true);
            foreach (var clase in PrimarioAPelo(limpio)) infractores.Add($"{ruta}: «{clase}»");
        }

        ficheros.Should().BeGreaterThan(150, "había 221 .razor al escribirlo; si esto es bajo, el recorrido dejó de ver el árbol real");

        string.Join(Environment.NewLine, infractores).Should().BeEmpty(
            "un primario es <Boton Variante=\"VarianteBoton.Primario\">: marcado a pelo con una clase «…boton-primario» no lo ve el " +
            "trinquete de un solo primario por superficie ni la hoja de contraste de Boton");
    }

    [Fact]
    public void El_detector_de_primario_a_pelo_ve_las_formas_reales_y_no_lo_parecido()
    {
        PrimarioAPelo("""<button type="submit" class="boton-primario">Guardar</button>""").Should().Equal("boton-primario");
        PrimarioAPelo("""<a class="boton-primario boton-continuar" href="x">Seguir</a>""").Should().Equal("boton-primario");
        PrimarioAPelo("""<button class="orden-menu-boton orden-menu-boton-primario">Guardar</button>""").Should().Equal("orden-menu-boton-primario");
        PrimarioAPelo("""<button class="boton @(esPrimario ? "boton-primario" : "boton-secundario")">X</button>""").Should().Equal("boton-primario");
        PrimarioAPelo("""<Boton Variante="VarianteBoton.Primario" class="boton-2fa">X</Boton>""").Should().BeEmpty("es el componente, con su clase de colocación");
        PrimarioAPelo("""<Boton Variante="VarianteBoton.Secundario" class="boton-primario">X</Boton>""").Should().Equal("boton-primario"); // la clase de primario no se cuela por el class de un componente
        PrimarioAPelo("""<button class="boton-primarioso">X</button>""").Should().BeEmpty("otro token que lo contiene");
        PrimarioAPelo("""<button class="boton-secundario">X</button>""").Should().BeEmpty();
    }

    /// <summary>Los tokens de clase que terminan en <c>boton-primario</c> dentro de un atributo <c>class</c> de la etiqueta de apertura.</summary>
    private static List<string> PrimarioAPelo(string razor)
    {
        var resultado = new List<string>();
        foreach (var etiqueta in MarcadoRazor.Aperturas(razor, @"[A-Za-z][A-Za-z0-9]*"))
        {
            var m = AtributoClase.Match(etiqueta.Texto);
            if (!m.Success) continue;
            var valor = MarcadoRazor.ValorDeComillas(etiqueta.Texto, m.Index + m.Length);
            foreach (Match token in TokenPrimario.Matches(valor)) resultado.Add(token.Value);
        }

        return resultado;
    }

    private static readonly Regex AtributoClase = new(@"(?:^|\s)class\s*=\s*""", RegexOptions.Compiled);

    private static readonly Regex TokenPrimario = new(@"(?<![\w-])(?:[\w]+-)*boton-primario(?![\w-])", RegexOptions.Compiled);

    // -------------------------------------------------------------------------------------------
    // 2. Un solo primario por superficie
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Una_superficie_con_mas_de_un_primario_esta_en_la_lista_con_su_numero_exacto()
    {
        var medido = MedirPrimariosPorSuperficie();

        // Control positivo: la medición ve superficies reales y la lista no está vacía por accidente.
        medido.Values.Sum().Should().BeGreaterThan(200, "había 277 primarios declarados al escribirlo; si baja de golpe, dejó de mirar");

        var problemas = Evaluar(medido, PrimariosPermitidosPorSuperficie);

        string.Join(Environment.NewLine, problemas).Should().BeEmpty(
            "un solo primario por superficie. Un primario de más se resuelve bajándolo a Secundario/Fantasma, no subiendo la lista; " +
            "si bajaste uno, baja su número en PrimariosPermitidosPorSuperficie (la lista solo puede decrecer)");
    }

    [Fact]
    public void Las_superficies_de_la_lista_existen_y_son_realmente_excepciones()
    {
        var raiz = MarcadoRazor.RaizDelRepositorio();
        var inexistentes = PrimariosPermitidosPorSuperficie.Keys
            .Select(k => k.Split('#')[0])
            .Where(ruta => !File.Exists(Path.Combine(raiz, ruta)))
            .ToList();

        inexistentes.Should().BeEmpty("el fichero se movió o se borró sin actualizar la lista");
        PrimariosPermitidosPorSuperficie.Values.Should().OnlyContain(n => n > 1, "una excepción permite más de uno; con uno o menos sobra");
    }

    /// <summary>
    /// La lista solo puede decrecer, y esto lo hace una propiedad y no una convención: pegar una entrada
    /// nueva, o subir un número, supera estos topes (los de la medición del 2026-10-02) y obliga a editar
    /// aquí, a la vista de la revisión, la decisión de dar un primario más a una pantalla.
    /// Al retirar o bajar entradas, bajan también estos dos números.
    /// </summary>
    [Fact]
    public void La_lista_de_excepciones_no_crece()
    {
        PrimariosPermitidosPorSuperficie.Count.Should().BeLessThanOrEqualTo(66, "entradas medidas el 2026-10-02 (64) más las dos de primarios marcados a pelo que se migraron a <Boton> y pasaron a contarse");
        PrimariosPermitidosPorSuperficie.Values.Sum().Should().BeLessThanOrEqualTo(199, "primarios en superficies excepcionales: 193 medidos el 2026-10-02 y 6 que no se veían por estar marcados a pelo (2FA 4, orden del menú 2); no es un primario nuevo");
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
    public void El_analizador_no_pierde_el_hilo_con_un_mayor_que_en_una_lambda_ni_con_un_literal_de_caracter()
    {
        // Si FinDeEtiqueta cerrara en el primer '>' (el de «=>»), el Variante que viene después de la lambda
        // quedaría fuera de la etiqueta y el botón parecería no declararla.
        var conLambdaAntes = Analizar("""<Boton OnClick="() => Abrir()" Variante="VarianteBoton.Primario">A</Boton>""");
        conLambdaAntes.Botones.Should().ContainSingle().Which.DeclaraVariante.Should().BeTrue();
        conLambdaAntes.Botones.Single().EsPrimario.Should().BeTrue();

        // Un ')' entre comillas simples no puede cerrar la expresión: sin esto, el cuerpo de este botón
        // absorbería al siguiente y le prestaría su Variante.
        var conCaracter = Analizar("""
            <Boton OnClick="() => Pulsar(')')">A</Boton>
            <Boton Variante="VarianteBoton.Secundario">B</Boton>
            """);
        conCaracter.Botones.Select(b => b.DeclaraVariante).Should().Equal(false, true);
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
                problemas.Add($"{superficie}: {n} primarios y sin excepción en la lista; deja uno y baja el resto a Secundario o Fantasma");
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

        var botones = MarcadoRazor.Aperturas(texto, "Boton").Select(a =>
        {
            var declara = AtributoVariante.Match(a.Texto);
            var valor = declara.Success ? MarcadoRazor.ValorDeComillas(a.Texto, declara.Index + declara.Length) : string.Empty;
            return new BotonAnalizado(a.Inicio, declara.Success, valor.Contains("VarianteBoton.Primario", StringComparison.Ordinal));
        }).ToList();

        var contenedores = MarcadoRazor.Elementos(texto, "Drawer|Modal|DialogoConfirmacion")
            .Where(e => !e.Autocerrado)
            .Select(e => new ContenedorAnalizado($"{e.Nombre}{e.Ordinal}", e.Inicio, e.Fin))
            .ToList();

        return new Analisis(botones, contenedores);
    }

    private static IEnumerable<(string Ruta, string Contenido)> LeerRazor() => MarcadoRazor.LeerRazorDeLaWeb();
}
