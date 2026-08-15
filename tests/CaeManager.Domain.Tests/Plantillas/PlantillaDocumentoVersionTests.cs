using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Plantillas;

public class PlantillaDocumentoVersionTests
{
    private static readonly string HashValido = new('a', PlantillaDocumentoVersion.LongitudHashSha256);

    private static PlantillaDocumentoVersion CrearVersion() =>
        new(Guid.NewGuid(), 1, "url/plantilla.pdf", HashValido);

    private static PlantillaElemento CrearElemento(Guid versionId) =>
        new(versionId, TipoElementoPlantilla.Texto, 1, 0, 0, 100, 20, "Razón social",
            fuenteDato: FuenteDatoPlantilla.EmpresaRazonSocial);

    [Fact]
    public void Nace_en_borrador_sin_elementos()
    {
        var version = CrearVersion();

        version.EstadoConfiguracion.Should().Be(EstadoConfiguracionPlantilla.Borrador);
        version.Elementos.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_rechaza_numero_de_version_menor_que_uno()
    {
        var accion = () => new PlantillaDocumentoVersion(Guid.NewGuid(), 0, "url/plantilla.pdf", HashValido);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_hash_con_longitud_incorrecta()
    {
        var accion = () => new PlantillaDocumentoVersion(Guid.NewGuid(), 1, "url/plantilla.pdf", "corto");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EstablecerElementos_reemplaza_el_conjunto_completo()
    {
        var version = CrearVersion();
        version.EstablecerElementos([CrearElemento(version.Id), CrearElemento(version.Id)]);

        version.EstablecerElementos([CrearElemento(version.Id)]);

        version.Elementos.Should().ContainSingle();
    }

    [Fact]
    public void MarcarPendienteRevision_cambia_el_estado()
    {
        var version = CrearVersion();

        version.MarcarPendienteRevision();

        version.EstadoConfiguracion.Should().Be(EstadoConfiguracionPlantilla.PendienteRevision);
    }

    [Fact]
    public void Confirmar_sin_elementos_falla()
    {
        var version = CrearVersion();

        var accion = () => version.Confirmar(Guid.NewGuid(), DateTime.UtcNow);

        accion.Should().Throw<InvalidOperationException>();
        version.EstadoConfiguracion.Should().Be(EstadoConfiguracionPlantilla.Borrador);
    }

    [Fact]
    public void Confirmar_rechaza_usuario_vacio()
    {
        var version = CrearVersion();
        version.EstablecerElementos([CrearElemento(version.Id)]);

        var accion = () => version.Confirmar(Guid.Empty, DateTime.UtcNow);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Confirmar_con_elementos_marca_la_version_como_confirmada()
    {
        var version = CrearVersion();
        version.EstablecerElementos([CrearElemento(version.Id)]);
        var usuarioId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;

        version.Confirmar(usuarioId, ahora);

        version.EstadoConfiguracion.Should().Be(EstadoConfiguracionPlantilla.Confirmada);
        version.ConfirmadaPorUsuarioId.Should().Be(usuarioId);
        version.ConfirmadaEnUtc.Should().Be(ahora);
    }

    [Fact]
    public void Una_version_confirmada_no_se_puede_volver_a_modificar()
    {
        var version = CrearVersion();
        version.EstablecerElementos([CrearElemento(version.Id)]);
        version.Confirmar(Guid.NewGuid(), DateTime.UtcNow);

        var accionEstablecer = () => version.EstablecerElementos([CrearElemento(version.Id)]);
        var accionConfirmarDeNuevo = () => version.Confirmar(Guid.NewGuid(), DateTime.UtcNow);
        var accionPendiente = () => version.MarcarPendienteRevision();

        accionEstablecer.Should().Throw<InvalidOperationException>();
        accionConfirmarDeNuevo.Should().Throw<InvalidOperationException>();
        accionPendiente.Should().Throw<InvalidOperationException>();
    }
}
