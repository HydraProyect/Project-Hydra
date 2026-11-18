using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// D-12: cada versión de un Documento nace con quien la sube. Propiedades del agregado: el autor se fija al nacer la
/// versión, no lo cambia nada después y una versión con autor no se renueva por una sin autor. Qué persona es la autora
/// lo decide Application (<c>AutorDeLaSubida</c>), con sus propios tests.
/// </summary>
public class DocumentoAutorVersionTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();
    private static readonly VigenciaDocumento Vigencia = VigenciaDocumento.VenceEl(Hoy.AddYears(1));
    private static readonly Guid Simulado = Guid.NewGuid();
    private static readonly Guid ActorReal = Guid.NewGuid();
    private static readonly AutorVersionDocumento EnImpersonacion = AutorVersionDocumento.Persona(Simulado, ActorReal);

    public static TheoryData<string> Ambitos => ["Trabajador", "Cliente", "Empresa", "Vehiculo", "Proyecto"];

    private static Documento Nuevo(string ambito, AutorVersionDocumento autor) => ambito switch
    {
        "Trabajador" => Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), Hoy, Vigencia, "a.pdf", autor: autor),
        "Cliente" => Documento.DeCliente(Guid.NewGuid(), Guid.NewGuid(), Hoy, Vigencia, "a.pdf", autor: autor),
        "Empresa" => Documento.DeEmpresa(Guid.NewGuid(), Guid.NewGuid(), Hoy, Vigencia, "a.pdf", autor: autor),
        "Vehiculo" => Documento.DeVehiculo(Guid.NewGuid(), Guid.NewGuid(), Hoy, Vigencia, "a.pdf", autor: autor),
        _ => Documento.DeProyecto(Guid.NewGuid(), Guid.NewGuid(), Hoy, Vigencia, "a.pdf", autor: autor),
    };

    [Theory]
    [MemberData(nameof(Ambitos))]
    public void La_version_nace_con_el_usuario_efectivo_y_el_Actor_real_por_separado(string ambito)
    {
        var documento = Nuevo(ambito, EnImpersonacion);

        documento.SubidoPorUsuarioId.Should().Be(Simulado);
        documento.SubidoPorActorRealUsuarioId.Should().Be(ActorReal);
    }

    [Theory]
    [MemberData(nameof(Ambitos))]
    public void Sin_autor_la_version_nace_sin_autor(string ambito)
    {
        var documento = Nuevo(ambito, AutorVersionDocumento.Desconocido);

        documento.SubidoPorUsuarioId.Should().BeNull();
        documento.SubidoPorActorRealUsuarioId.Should().BeNull();
    }

    [Fact]
    public void Sin_pasar_el_autor_la_factoria_deja_la_version_sin_autor_como_un_seeder()
    {
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), Hoy, Vigencia);

        documento.SubidoPorUsuarioId.Should().BeNull();
        documento.SubidoPorActorRealUsuarioId.Should().BeNull();
    }

    [Fact]
    public void La_version_renovada_es_de_quien_la_sube_y_la_anterior_conserva_su_autor()
    {
        var subeAntes = AutorVersionDocumento.Persona(Guid.NewGuid(), Guid.NewGuid());
        var anterior = Nuevo("Trabajador", subeAntes);

        var nueva = anterior.NuevoDelMismoTitular(Hoy, Vigencia, "b.pdf", null, EnImpersonacion);

        nueva.SubidoPorUsuarioId.Should().Be(Simulado);
        nueva.SubidoPorActorRealUsuarioId.Should().Be(ActorReal);
        anterior.SubidoPorUsuarioId.Should().Be(subeAntes.UsuarioId);
        anterior.SubidoPorActorRealUsuarioId.Should().Be(subeAntes.ActorRealUsuarioId);
    }

    [Fact]
    public void Una_version_con_autor_no_se_renueva_por_una_sin_autor()
    {
        var anterior = Nuevo("Empresa", EnImpersonacion);

        var renovar = () => anterior.NuevoDelMismoTitular(Hoy, Vigencia, "b.pdf", null, AutorVersionDocumento.Desconocido);

        renovar.Should().Throw<InvalidOperationException>().WithMessage("*D-12*");
    }

    [Fact]
    public void Una_version_sin_autor_se_renueva_con_o_sin_autor()
    {
        var anterior = Nuevo("Empresa", AutorVersionDocumento.Desconocido);

        anterior.NuevoDelMismoTitular(Hoy, Vigencia, "b.pdf", null, AutorVersionDocumento.Desconocido)
            .SubidoPorUsuarioId.Should().BeNull();
        anterior.NuevoDelMismoTitular(Hoy, Vigencia, "c.pdf", null, EnImpersonacion)
            .SubidoPorUsuarioId.Should().Be(Simulado);
    }

    [Fact]
    public void Adjuntar_un_archivo_a_la_misma_version_no_cambia_su_autor()
    {
        var documento = Nuevo("Trabajador", EnImpersonacion);

        documento.AdjuntarArchivo("firmado.pdf");

        documento.SubidoPorUsuarioId.Should().Be(Simulado);
        documento.SubidoPorActorRealUsuarioId.Should().Be(ActorReal);
    }

    [Fact]
    public void Un_autor_conocido_exige_los_dos_identificadores()
    {
        AutorVersionDocumento.Desconocido.EsConocido.Should().BeFalse();
        default(AutorVersionDocumento).Should().Be(AutorVersionDocumento.Desconocido);
        EnImpersonacion.EsConocido.Should().BeTrue();

        ((Action)(() => AutorVersionDocumento.Persona(Guid.Empty, ActorReal))).Should().Throw<ArgumentException>();
        ((Action)(() => AutorVersionDocumento.Persona(Simulado, Guid.Empty))).Should().Throw<ArgumentException>();
    }
}
