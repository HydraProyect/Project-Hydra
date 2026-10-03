using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// S12 (lote 3a, 2026-10-03). Un formulario en <c>Modal</c> (campos de alta o edición dentro de un Modal) se escribe con el kit
/// <c>ModalFormulario</c>, no con un <c>&lt;Modal&gt;</c> artesanal: el hermano de <c>FormulariosEnDrawerUsanElKitTests</c> y con las
/// mismas tres propiedades, por separado:
///
/// <list type="number">
/// <item><b>Ningún <c>&lt;Modal&gt;</c> con campos fuera del kit, salvo los congelados.</b> Lista congelada por ubicación
/// (<c>Congelados/Modal-formulario-fuera-del-kit.txt</c>, <see cref="ListaCongelada"/>): un Modal con campos nuevo y propio es una
/// línea que falta (<c>NUEVA</c>) y falla siempre; migrar uno al kit baja la lista (<c>OBSOLETA</c>/<c>BAJA</c>).</item>
/// <item><b>El kit obliga a lo que el artesanal dejaba opcional:</b> <c>HayCambios</c>, <c>Titulo</c>, <c>TextoGuardar</c> y
/// <c>TextoCancelar</c> son <c>[EditorRequired]</c> (RZ2012: con <c>-warnaserror</c> su falta no compila).</item>
/// <item><b>Usar el kit no desactiva el guardián:</b> <c>&lt;ModalFormulario HayCambios="() =&gt; false"&gt;</c> se prohíbe igual
/// que un Modal sin <c>HayCambios</c>.</item>
/// </list>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> «Campo» y «contenedor» son los de
/// <c>FormulariosEnDrawerUsanElKitTests</c>; el campo se atribuye al contenedor más interior (los kits cuentan como contenedor:
/// un campo dentro de <c>&lt;ModalFormulario&gt;</c> es del kit aunque el kit esté dentro de un <c>Drawer</c>). NO vigila: el
/// <c>&lt;DialogoConfirmacion&gt;</c> con un «Motivo (opcional)» (es el cuerpo de una confirmación, no un formulario; ver
/// <c>DrawerYModalConCamposPreguntanAlDescartarTests</c>), un Modal de varios pasos o de solo lectura (sin campos), ni los campos
/// de un componente hijo que el Modal monta (<c>FormularioRapidoCliente</c> es un <c>Modal</c> propio y sí se mide en su fichero).
/// NO ve que el kit se use <i>bien</i> (un <c>HayCambios</c> que cubra todos los campos): eso lo prueba el bUnit de cada pantalla. Un
/// <c>&lt;input&gt;</c> escrito en un fichero ya congelado no sube el recuento (cuenta Modales, no campos).
/// </para>
/// </summary>
public class FormulariosEnModalUsanElKitTests
{
    private const string Kit = "src/CaeManager.Web/Components/DesignSystem/ModalFormulario.razor";
    private const string Lista = "Modal-formulario-fuera-del-kit";
    private const string Contenedores = "Drawer|Modal|DialogoConfirmacion|DrawerFormulario|ModalFormulario";

    private const string Campos =
        "CampoTexto|CampoSelect|CampoTextarea|CampoBuscarSelect|SelectorEntidad|SelectorMultiple|ZonaSoltarArchivo|" +
        "InputText|InputTextArea|InputNumber|InputSelect|InputDate|InputCheckbox|InputFile|textarea|select|input";

    private static readonly Regex AtributoHayCambios = new(@"(?:^|\s)HayCambios\s*=\s*""", RegexOptions.Compiled);
    private static readonly string[] GuardianesInertes = ["false", "()=>false", "(()=>false)", "()=>{returnfalse;}"];

    private const string Guia =
        "Un formulario en Modal se escribe con <ModalFormulario> (guardián, «Cancelar», «Guardar» único, aviso y pie ya vienen resueltos). " +
        "Si migras una pantalla al kit, baja o borra su línea en tests/CaeManager.Architecture.Tests/Congelados/" + Lista + ".txt " +
        "(HYDRA_TRINQUETES_VOLCAR=<directorio> vuelca la medida). No añadas una línea para un Modal nuevo.";

    [Fact]
    public void Ningun_Modal_con_campos_queda_fuera_del_kit_salvo_los_congelados()
    {
        var medido = MedirFueraDelKit(MarcadoRazor.LeerRazorDeLaWeb());

        // Control positivo: si el recorrido no viera Modales con campos, «ninguno nuevo» valdría por vacío.
        medido.Values.Sum().Should().BeGreaterThan(20, "había 28 Modales con campos al escribirlo (27 tras el piloto Guardar filtro, 23 tras el lote 3b-1); el umbral se ajusta a lo medido en cada lote que migra; si baja de golpe sin migrar nada, dejó de mirar");

        ListaCongelada.Verificar(Lista, medido, Guia).Should().BeNull();
    }

    [Fact]
    public void El_kit_existe_y_lo_usa_al_menos_una_pantalla_migrada()
    {
        var razor = MarcadoRazor.LeerRazorDeLaWeb().ToList();

        razor.Should().Contain(f => f.Ruta == Kit);
        razor.Where(f => f.Ruta != Kit && Regex.IsMatch(LimpiadorDeComentarios.Quitar(f.Contenido, razor: true), @"<ModalFormulario(?=[\s>/])"))
            .Should().NotBeEmpty("la pantalla piloto migrada («Guardar filtro» de Clientes) es la prueba de que el kit se puede usar de verdad");
    }

    [Theory]
    [InlineData(nameof(ModalFormulario.HayCambios))]
    [InlineData(nameof(ModalFormulario.Titulo))]
    [InlineData(nameof(ModalFormulario.TextoGuardar))]
    [InlineData(nameof(ModalFormulario.TextoCancelar))]
    public void El_kit_exige_lo_que_el_formulario_artesanal_dejaba_opcional(string parametro)
    {
        var propiedad = typeof(ModalFormulario).GetProperty(parametro)!;

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
            foreach (var apertura in MarcadoRazor.Aperturas(texto, "ModalFormulario"))
            {
                if (!GuardianReal(apertura.Texto))
                    problemas.Add($"{ruta}: <ModalFormulario> sin HayCambios real (falta o es un literal que nunca pregunta)");
            }
        }

        string.Join(Environment.NewLine, problemas).Should().BeEmpty(
            "HayCambios es el guardián del formulario: una función que devuelve false siempre tira lo escrito sin preguntar");
    }

    [Fact]
    public void Ninguna_pantalla_escribe_a_mano_el_aviso_del_formulario_dentro_del_kit()
    {
        var problemas = new List<string>();

        foreach (var (ruta, contenido) in MarcadoRazor.LeerRazorDeLaWeb().Where(f => f.Ruta != Kit))
            problemas.AddRange(AvisosAMano(ruta, contenido));

        string.Join(Environment.NewLine, problemas).Should().BeEmpty(
            "el aviso del formulario es MensajeError (o Aviso) del kit: escrito a mano dentro del cuerpo se va con el desplazamiento (D-20)");
    }

    private static IEnumerable<string> AvisosAMano(string ruta, string contenido)
    {
        var texto = LimpiadorDeComentarios.Quitar(contenido, razor: true);
        foreach (var kit in MarcadoRazor.Elementos(texto, "ModalFormulario").Where(e => !e.Autocerrado))
        {
            if (Regex.IsMatch(kit.Cuerpo, @"alerta-formulario|role\s*=\s*""alert""|<AvisoFormulario(?=[\s/>])"))
                yield return $"{ruta}#ModalFormulario{kit.Ordinal}: aviso escrito a mano (alerta-formulario, role=\"alert\" o <AvisoFormulario>) dentro del kit; usa MensajeError o Aviso";
        }
    }

    /// <summary>Control positivo de las medidas, con fuentes sintéticas y nunca con una entrada de la lista.</summary>
    [Fact]
    public void Las_medidas_ven_lo_que_vigilan_y_no_lo_parecido()
    {
        const string artesanal = "<Modal Visible=\"v\"><ChildContent><CampoTexto Valor=\"x\" /></ChildContent></Modal>";
        MedirFueraDelKit([("a.razor", artesanal)]).Should().ContainSingle().Which.Key.Should().Be(new Ubicacion("a.razor", "Modal"));
        MedirFueraDelKit([("a.razor", artesanal + artesanal)]).Should().ContainSingle().Which.Value.Should().Be(2);

        MedirFueraDelKit([("a.razor", "<Modal Visible=\"v\"><p>solo lectura</p></Modal>")]).Should().BeEmpty("sin campos no es un formulario");
        MedirFueraDelKit([("a.razor", "<ModalFormulario Visible=\"v\"><CampoTexto Valor=\"x\" /></ModalFormulario>")]).Should().BeEmpty(
            "<ModalFormulario> es el kit, no un Modal artesanal");
        MedirFueraDelKit([("a.razor", "<Drawer><ModalFormulario><CampoTexto Valor=\"x\" /></ModalFormulario></Drawer>")]).Should().BeEmpty(
            "el campo es del kit más interior, no del Drawer que lo rodea");
        MedirFueraDelKit([("a.razor", "<DrawerFormulario><Dialogos><Modal><CampoTexto Valor=\"x\" /></Modal></Dialogos></DrawerFormulario>")]).Should().ContainSingle(
            "un Modal artesanal con campos dentro de los Dialogos de un DrawerFormulario sigue siendo un Modal artesanal");
        MedirFueraDelKit([("a.razor", "<DialogoConfirmacion><CampoTextarea Valor=\"x\" /></DialogoConfirmacion>")]).Should().BeEmpty(
            "el «Motivo (opcional)» de una confirmación no es un formulario en Modal");
        MedirFueraDelKit([("a.razor", "@* <Modal><CampoTexto /></Modal> *@")]).Should().BeEmpty("un comentario de Razor no cuenta");
        MedirFueraDelKit([("a.razor", "<Modal><input type=\"hidden\" /></Modal>")]).Should().BeEmpty("un hidden no es un campo");
        MedirFueraDelKit([("a.razor", "<Modal><Drawer><CampoTexto /></Drawer></Modal>")]).Should().BeEmpty("el campo es del Drawer más interior, que vigila otro test");
        MedirFueraDelKit([(Kit, artesanal)]).Should().BeEmpty("el propio kit monta el Modal");

        AvisosAMano("a.razor", "<ModalFormulario><div class=\"alerta-formulario\" role=\"alert\">x</div></ModalFormulario>").Should().ContainSingle();
        AvisosAMano("a.razor", "<ModalFormulario><AvisoFormulario Mensaje=\"@x\" /></ModalFormulario>").Should().ContainSingle(
            "el AvisoFormulario dentro del cuerpo reintroduce el aviso que se va con el desplazamiento");
        AvisosAMano("a.razor", "<ModalFormulario><CampoTexto MensajeError=\"x\" /></ModalFormulario>").Should().BeEmpty("el error de un campo lo pinta el campo");
        AvisosAMano("a.razor", "<div role=\"alert\">fuera</div><ModalFormulario><CampoTexto /></ModalFormulario>").Should().BeEmpty("fuera del kit no es de este test");

        GuardianReal("<ModalFormulario HayCambios=\"() => HayCambiosFiltro\" Visible=\"v\">").Should().BeTrue();
        GuardianReal("<ModalFormulario HayCambios=\"() => false\" Visible=\"v\">").Should().BeFalse();
        GuardianReal("<ModalFormulario Visible=\"v\">").Should().BeFalse();
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
                if (dueno is { Nombre: "Modal" })
                    conCampos.Add(dueno);
            }

            if (conCampos.Count > 0)
                medido[new Ubicacion(ruta, "Modal")] = conCampos.Count;
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
