using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// DEC-34/36 (REC-132): clasificación canónica de sensibilidad documental,
/// propuesta del catálogo semilla — revisable por el propietario, ver
/// <c>tecnico/docs/SENSIBILIDAD-DOCUMENTAL.md</c> en el repositorio de
/// negocio. Este ratchet fija dos cosas: que <b>ningún</b> tipo del catálogo
/// se quede sin una clasificación explícita, y las afirmaciones concretas que
/// más importa no perder por descuido — los dos tipos que revelan salud.
/// </summary>
public class SensibilidadDelCatalogoSemillaTests
{
    private static TipoDocumento Buscar(string nombre) =>
        TipoDocumentoSeedData.CrearCopiasParaTenant().Single(t => t.Nombre == nombre);

    /// <summary>
    /// El único par verificado como categoría especial de salud: DEC-34/36
    /// da el reconocimiento médico como ejemplo literal, y el informe de
    /// accidente puede describir la lesión del trabajador. Si alguno de los
    /// dos se quitara de <c>TipoDocumentoSeedData.SensibilidadPorNombre</c>,
    /// este test tiene que ponerse en rojo por perder la afirmación —
    /// no solo por dejar de "estar clasificado".
    /// </summary>
    [Theory]
    [InlineData("Certificado de aptitud médica")]
    [InlineData("Informe de investigación de accidente o incidente")]
    public void Los_tipos_que_revelan_salud_estan_en_la_categoria_especial(string nombre)
    {
        Buscar(nombre).RevelaSalud.Should().BeTrue(
            $"'{nombre}' revela información sobre salud física o mental de una persona identificada (DEC-34/36)");
    }

    /// <summary>
    /// El filo que protege de la sobre-clasificación: un tipo verificado como
    /// dato personal ordinario no puede deslizarse a categoría especial de
    /// salud sin que alguien lo decida explícitamente, porque eso diluiría la
    /// señal que REC-036/REC-099 necesitan para actuar solo donde hace falta.
    /// </summary>
    [Theory]
    [InlineData("Entrega de EPI")]
    [InlineData("Documento de identidad")]
    [InlineData("Registro retributivo")]
    public void Los_datos_personales_ordinarios_no_se_confunden_con_salud(string nombre)
    {
        Buscar(nombre).RevelaSalud.Should().BeFalse(
            $"'{nombre}' identifica a una persona pero no revela su salud");
        Buscar(nombre).Sensibilidad.Should().Be(SensibilidadDocumental.DatosPersonales);
    }

    /// <summary>
    /// Documentos de Empresa/Vehículo que no nombran a ninguna persona.
    /// </summary>
    [Theory]
    [InlineData("Certificado de estar al corriente con la Seguridad Social")]
    [InlineData("Evaluación de Riesgos Laborales")]
    [InlineData("Ficha técnica")]
    public void Lo_que_no_identifica_a_nadie_no_tiene_datos_personales(string nombre)
    {
        Buscar(nombre).Sensibilidad.Should().Be(SensibilidadDocumental.SinDatosPersonales);
    }

    /// <summary>
    /// El ratchet real: cada nombre del catálogo semilla tiene que aparecer
    /// como clave explícita en <c>SensibilidadPorNombre</c>. A diferencia del
    /// equivalente para <see cref="NaturalezaJuridica"/> (que solo comprueba
    /// que el enum no admite nulos, y por tanto siempre pasa), este SÍ puede
    /// fallar: un tipo nuevo en <c>TipoDocumentoSeedData.Datos</c> sin su
    /// entrada correspondiente cae en el valor por defecto sin que nadie lo
    /// haya decidido — que es justo lo que "ninguno se queda sin clasificar"
    /// significa en HO-132-01 § 9.6.
    /// </summary>
    [Fact]
    public void Ningun_tipo_del_catalogo_se_queda_sin_clasificacion_propuesta()
    {
        var nombresDelCatalogo = TipoDocumentoSeedData.Datos.Select(d => d.Nombre).ToList();

        nombresDelCatalogo.Should().NotBeEmpty();
        nombresDelCatalogo.Should().OnlyHaveUniqueItems(
            "la clasificación se busca por nombre exacto — un nombre repetido significaría que una de las dos filas nunca se alcanza");

        var sinClasificar = nombresDelCatalogo
            .Where(nombre => !TipoDocumentoSeedData.SensibilidadPorNombre.ContainsKey(nombre))
            .OrderBy(n => n)
            .ToList();

        string.Join(Environment.NewLine, sinClasificar).Should().BeEmpty(
            "todo tipo del catálogo semilla necesita una clasificación propuesta explícita con motivo (HO-132-01 § 9.2), " +
            "no puede depender en silencio del valor por defecto");
    }

    /// <summary>
    /// El motivo no puede quedarse vacío: es lo que hace la propuesta
    /// revisable por el propietario en vez de una lista sin justificar.
    /// </summary>
    [Fact]
    public void Toda_clasificacion_propuesta_tiene_un_motivo_no_vacio()
    {
        TipoDocumentoSeedData.SensibilidadPorNombre.Should().OnlyContain(
            entrada => !string.IsNullOrWhiteSpace(entrada.Value.Motivo));
    }
}
