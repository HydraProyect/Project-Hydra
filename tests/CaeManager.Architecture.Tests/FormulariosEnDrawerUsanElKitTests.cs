using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// S12 (fase 2 de salvaguardas, lote 1, 2026-10-03). Un formulario en <c>Drawer</c> (campos de creación o edición dentro de
/// un Drawer) se escribe con el kit <c>DrawerFormulario</c>, no con un <c>&lt;Drawer&gt;</c> artesanal. El patrón compartido
/// existía pero era opcional (C1 del análisis de causas raíz: 14 de los 33 defectos D-01..D-33 son un patrón de UI que cada
/// pantalla reescribía): guardián de cambios (D-05, D-20), «Cancelar» que no pasa por el guardián (D-05), aviso del formulario
/// fuera de la vista (D-20), primario y estado de guardado a mano (D-30). Tres propiedades, por separado:
///
/// <list type="number">
/// <item><b>Ningún <c>&lt;Drawer&gt;</c> con campos fuera del kit, salvo los congelados.</b> Lista congelada por ubicación
/// (<c>Congelados/Drawer-formulario-fuera-del-kit.txt</c>, <see cref="ListaCongelada"/>): una pantalla nueva con un Drawer
/// de campos propio es una línea que falta (<c>NUEVA</c>) y falla siempre; migrar una al kit baja la lista (<c>OBSOLETA</c>/
/// <c>BAJA</c>, que dicen qué línea tocar). No se puede «subir el número» sin que el diff enseñe qué fichero.</item>
/// <item><b>El kit obliga a lo que el artesanal dejaba opcional.</b> <c>HayCambios</c>, <c>Titulo</c>, <c>TextoGuardar</c> y
/// <c>TextoCancelar</c> son <c>[EditorRequired]</c> (RZ2012: con <c>-warnaserror</c>, el que CI usa, su falta no compila).</item>
/// <item><b>Usar el kit no desactiva el guardián:</b> <c>&lt;DrawerFormulario HayCambios="() =&gt; false"&gt;</c> es el mismo
/// agujero que el Drawer sin <c>HayCambios</c> y se prohíbe igual.</item>
/// </list>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> «Campo» y «contenedor» son los de
/// <c>DrawerYModalConCamposPreguntanAlDescartarTests</c> (<c>CampoTexto</c>, <c>CampoSelect</c>… y <c>&lt;input&gt;</c>/
/// <c>&lt;select&gt;</c>/<c>&lt;textarea&gt;</c> crudos, salvo <c>hidden</c>; el campo se atribuye al contenedor más interior).
/// Solo se vigila el <c>Drawer</c>: un formulario en <c>Modal</c> (hoy ~15) y el de un componente hijo que el Drawer monta no
/// entran (lote 2: <c>ModalFormulario</c>); tampoco los Drawer de solo lectura (vista previa, detalle), que no tienen campos.
/// NO ve que el kit se use <i>bien</i> (campos dentro, un <c>HayCambios</c> que cubra todos): eso lo prueba el bUnit de cada
/// pantalla. Un <c>&lt;input&gt;</c> escrito en un fichero que ya está congelado no sube el recuento (cuenta Drawers, no campos).
/// </para>
/// </summary>
public class FormulariosEnDrawerUsanElKitTests
{
    private const string Kit = "src/CaeManager.Web/Components/DesignSystem/DrawerFormulario.razor";
    private const string Lista = "Drawer-formulario-fuera-del-kit";
    private const string Contenedores = "Drawer|Modal|DialogoConfirmacion";

    private const string Campos =
        "CampoTexto|CampoSelect|CampoTextarea|CampoBuscarSelect|SelectorEntidad|SelectorMultiple|ZonaSoltarArchivo|" +
        "InputText|InputTextArea|InputNumber|InputSelect|InputDate|InputCheckbox|InputFile|textarea|select|input";

    private static readonly Regex AtributoHayCambios = new(@"(?:^|\s)HayCambios\s*=\s*""", RegexOptions.Compiled);
    private static readonly string[] GuardianesInertes = ["false", "()=>false", "(()=>false)", "()=>{returnfalse;}"];

    private const string Guia =
        "Un formulario en Drawer se escribe con <DrawerFormulario> (guardián, «Cancelar», aviso y pie ya vienen resueltos). " +
        "Si migras una pantalla al kit, baja o borra su línea en tests/CaeManager.Architecture.Tests/Congelados/" + Lista + ".txt " +
        "(HYDRA_TRINQUETES_VOLCAR=<directorio> vuelca la medida). No añadas una línea para un Drawer nuevo.";

    [Fact]
    public void Ningun_Drawer_con_campos_queda_fuera_del_kit_salvo_los_congelados()
    {
        var medido = MedirFueraDelKit(MarcadoRazor.LeerRazorDeLaWeb());

        // Control positivo: si el recorrido no viera Drawers con campos, «ninguno nuevo» valdría por vacío.
        medido.Values.Sum().Should().BeGreaterThan(15, "había 25 Drawers con campos en 21 ficheros al escribirlo (26 en 22 antes de migrar Trabajadores); si baja de golpe, dejó de mirar");

        ListaCongelada.Verificar(Lista, medido, Guia).Should().BeNull();
    }

    [Fact]
    public void El_kit_existe_y_lo_usa_al_menos_una_pantalla_migrada()
    {
        var razor = MarcadoRazor.LeerRazorDeLaWeb().ToList();

        razor.Should().Contain(f => f.Ruta == Kit);
        razor.Where(f => f.Ruta != Kit && Regex.IsMatch(LimpiadorDeComentarios.Quitar(f.Contenido, razor: true), @"<DrawerFormulario(?=[\s>/])"))
            .Should().NotBeEmpty("la pantalla dorada migrada es la prueba de que el kit se puede usar de verdad");
    }

    [Theory]
    [InlineData(nameof(DrawerFormulario.HayCambios))]
    [InlineData(nameof(DrawerFormulario.Titulo))]
    [InlineData(nameof(DrawerFormulario.TextoGuardar))]
    [InlineData(nameof(DrawerFormulario.TextoCancelar))]
    public void El_kit_exige_lo_que_el_formulario_artesanal_dejaba_opcional(string parametro)
    {
        var propiedad = typeof(DrawerFormulario).GetProperty(parametro)!;

        propiedad.GetCustomAttribute<ParameterAttribute>().Should().NotBeNull();
        propiedad.GetCustomAttribute<EditorRequiredAttribute>().Should().NotBeNull(
            $"{parametro} obligatorio: RZ2012 con -warnaserror hace que olvidarlo no compile (D-05, D-20, D-30)");
    }

    [Fact]
    public void Ninguna_pantalla_desactiva_el_guardian_del_kit()
    {
        var problemas = new List<string>();

        foreach (var (ruta, contenido) in MarcadoRazor.LeerRazorDeLaWeb().Where(f => f.Ruta != Kit))
        {
            var texto = LimpiadorDeComentarios.Quitar(contenido, razor: true);
            foreach (var apertura in MarcadoRazor.Aperturas(texto, "DrawerFormulario"))
            {
                if (!GuardianReal(apertura.Texto))
                    problemas.Add($"{ruta}: <DrawerFormulario> sin HayCambios real (falta o es un literal que nunca pregunta)");
            }
        }

        string.Join(Environment.NewLine, problemas).Should().BeEmpty(
            "HayCambios es el guardián del formulario: una función que devuelve false siempre tira lo escrito sin preguntar");
    }

    /// <summary>
    /// La lista <see cref="Campos"/> es de nombres: un componente de campo nuevo en el sistema de diseño que no figure en ella
    /// haría invisibles a este detector (y al de <c>DrawerYModalConCamposPreguntanAlDescartarTests</c>) los Drawer que lo usen.
    /// </summary>
    [Fact]
    public void Todo_componente_de_campo_del_sistema_de_diseno_esta_en_la_lista_de_campos()
    {
        var diseno = Path.Combine(MarcadoRazor.RaizDelRepositorio(), "src", "CaeManager.Web", "Components", "DesignSystem");
        var conocidos = Campos.Split('|').ToHashSet(StringComparer.Ordinal);

        var campos = Directory.EnumerateFiles(diseno, "*.razor")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n!.StartsWith("Campo", StringComparison.Ordinal) || n.StartsWith("Selector", StringComparison.Ordinal))
            .ToList();

        campos.Should().HaveCountGreaterThan(5, "había 6 al escribirlo (CampoTexto, CampoSelect, CampoTextarea, CampoBuscarSelect, SelectorEntidad, SelectorMultiple); si baja, dejó de mirar");
        campos.Where(n => !conocidos.Contains(n!)).Should().BeEmpty(
            "un componente de campo nuevo se añade a Campos de este test y del de DrawerYModalConCamposPreguntanAlDescartarTests");
    }

    [Fact]
    public void Ninguna_pantalla_escribe_a_mano_el_aviso_del_formulario_dentro_del_kit()
    {
        var problemas = new List<string>();

        foreach (var (ruta, contenido) in MarcadoRazor.LeerRazorDeLaWeb().Where(f => f.Ruta != Kit))
            problemas.AddRange(AvisosAMano(ruta, contenido));

        string.Join(Environment.NewLine, problemas).Should().BeEmpty(
            "el aviso del formulario es MensajeError del kit: escrito a mano dentro del cuerpo se va con el desplazamiento y no se ve (D-20)");
    }

    private static IEnumerable<string> AvisosAMano(string ruta, string contenido)
    {
        var texto = LimpiadorDeComentarios.Quitar(contenido, razor: true);
        foreach (var kit in MarcadoRazor.Elementos(texto, "DrawerFormulario").Where(e => !e.Autocerrado))
        {
            if (Regex.IsMatch(kit.Cuerpo, @"alerta-formulario|role\s*=\s*""alert""|<AvisoFormulario(?=[\s/>])"))
                yield return $"{ruta}#DrawerFormulario{kit.Ordinal}: aviso escrito a mano (alerta-formulario, role=\"alert\" o <AvisoFormulario>) dentro del kit; usa MensajeError";
        }
    }

    /// <summary>Control positivo de las dos medidas, con fuentes sintéticas y nunca con una entrada de la lista.</summary>
    [Fact]
    public void Las_medidas_ven_lo_que_vigilan_y_no_lo_parecido()
    {
        const string artesanal = "<Drawer Visible=\"v\"><ChildContent><CampoTexto Valor=\"x\" /></ChildContent></Drawer>";
        MedirFueraDelKit([("a.razor", artesanal)]).Should().ContainSingle().Which.Key.Should().Be(new Ubicacion("a.razor", "Drawer"));
        MedirFueraDelKit([("a.razor", artesanal + artesanal)]).Should().ContainSingle().Which.Value.Should().Be(2);

        MedirFueraDelKit([("a.razor", "<Drawer Visible=\"v\"><p>solo lectura</p></Drawer>")]).Should().BeEmpty("sin campos no es un formulario");
        MedirFueraDelKit([("a.razor", "<DrawerFormulario Visible=\"v\"><CampoTexto Valor=\"x\" /></DrawerFormulario>")]).Should().BeEmpty(
            "<DrawerFormulario> es el kit, no un Drawer artesanal");
        MedirFueraDelKit([("a.razor", "@* <Drawer><CampoTexto /></Drawer> *@")]).Should().BeEmpty("un comentario de Razor no cuenta");
        MedirFueraDelKit([("a.razor", "<Drawer><input type=\"hidden\" /></Drawer>")]).Should().BeEmpty("un hidden no es un campo");
        MedirFueraDelKit([("a.razor", "<Drawer><Modal><CampoTexto /></Modal></Drawer>")]).Should().BeEmpty(
            "el campo es del Modal más interior, que este test no vigila");
        MedirFueraDelKit([(Kit, artesanal)]).Should().BeEmpty("el propio kit monta el Drawer");

        AvisosAMano("a.razor", "<DrawerFormulario><div class=\"alerta-formulario\" role=\"alert\">x</div></DrawerFormulario>").Should().ContainSingle();
        AvisosAMano("a.razor", "<DrawerFormulario><p role=\"alert\">x</p></DrawerFormulario>").Should().ContainSingle();
        AvisosAMano("a.razor", "<DrawerFormulario><AvisoFormulario Mensaje=\"@x\" /></DrawerFormulario>").Should().ContainSingle(
            "el AvisoFormulario dentro del cuerpo reintroduce el aviso que se va con el desplazamiento");
        AvisosAMano("a.razor", "<DrawerFormulario><CampoTexto MensajeError=\"x\" /></DrawerFormulario>").Should().BeEmpty("el error de un campo lo pinta el campo");
        AvisosAMano("a.razor", "<div role=\"alert\">fuera</div><DrawerFormulario><CampoTexto /></DrawerFormulario>").Should().BeEmpty("fuera del kit no es de este test");

        GuardianReal("<DrawerFormulario HayCambios=\"() => HayCambiosAlta\" Visible=\"v\">").Should().BeTrue();
        GuardianReal("<DrawerFormulario HayCambios=\"() => false\" Visible=\"v\">").Should().BeFalse();
        GuardianReal("<DrawerFormulario Visible=\"v\">").Should().BeFalse();
    }

    private static Dictionary<Ubicacion, int> MedirFueraDelKit(IEnumerable<(string Ruta, string Contenido)> ficheros)
    {
        var medido = new Dictionary<Ubicacion, int>();

        foreach (var (ruta, contenido) in ficheros.Where(f => f.Ruta != Kit))
        {
            var texto = LimpiadorDeComentarios.Quitar(contenido, razor: true);
            var contenedores = MarcadoRazor.Elementos(texto, Contenedores).Where(e => !e.Autocerrado).ToList();
            var conCampos = new HashSet<MarcadoRazor.Elemento>();

            foreach (var campo in MarcadoRazor.Aperturas(texto, Campos).Where(a => !EsInputOculto(a)))
            {
                var dueno = contenedores
                    .Where(c => c.Inicio < campo.Inicio && campo.Inicio < c.Fin)
                    .OrderByDescending(c => c.Inicio)
                    .FirstOrDefault();
                if (dueno is { Nombre: "Drawer" })
                    conCampos.Add(dueno);
            }

            if (conCampos.Count > 0)
                medido[new Ubicacion(ruta, "Drawer")] = conCampos.Count;
        }

        return medido;
    }

    private static bool EsInputOculto(MarcadoRazor.Apertura a) =>
        a.Nombre == "input" && Regex.IsMatch(a.Texto, @"\btype\s*=\s*""hidden""");

    private static bool GuardianReal(string apertura)
    {
        var m = AtributoHayCambios.Match(apertura);
        if (!m.Success) return false;
        var valor = Regex.Replace(MarcadoRazor.ValorDeComillas(apertura, m.Index + m.Length), @"\s+", string.Empty).TrimStart('@');
        return !GuardianesInertes.Contains(valor);
    }
}
