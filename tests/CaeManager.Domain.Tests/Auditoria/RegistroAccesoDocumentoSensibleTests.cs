using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Auditoria;

public class RegistroAccesoDocumentoSensibleTests
{
    private static RegistroAccesoDocumentoSensible Crear(
        TipoViaAccesoAuditoria via = TipoViaAccesoAuditoria.Normal,
        SensibilidadDocumental sensibilidad = SensibilidadDocumental.CategoriaEspecialSalud) =>
        new(Guid.NewGuid(), sensibilidad, TipoAccesoDocumentoSensible.Apertura, Guid.NewGuid(), Guid.NewGuid(), via, null);

    [Fact]
    public void Un_documento_vacio_no_es_un_registro_valido()
    {
        var construir = () => new RegistroAccesoDocumentoSensible(
            Guid.Empty, SensibilidadDocumental.CategoriaEspecialSalud, TipoAccesoDocumentoSensible.Apertura,
            Guid.NewGuid(), Guid.NewGuid(), TipoViaAccesoAuditoria.Normal, null);

        construir.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EsPrivilegiado_solo_es_verdadero_bajo_sesion_privilegiada()
    {
        Crear(TipoViaAccesoAuditoria.SesionPrivilegiada).EsPrivilegiado.Should().BeTrue();
        Crear(TipoViaAccesoAuditoria.Normal).EsPrivilegiado.Should().BeFalse();
        Crear(TipoViaAccesoAuditoria.OperacionDelegada).EsPrivilegiado.Should().BeFalse();
    }

    [Fact]
    public void OcurridoEnUtc_se_fija_al_construir()
    {
        var antes = DateTime.UtcNow;
        var registro = Crear();
        var despues = DateTime.UtcNow;

        registro.OcurridoEnUtc.Should().BeOnOrAfter(antes).And.BeOnOrBefore(despues);
    }

    /// <summary>
    /// DEC-36: "no permitir modificación ni borrado ordinario de estos
    /// eventos". Es una garantía de diseño (sin método público que mute
    /// estado tras construir), y este test la deja explícita y vigilada: si
    /// alguien añade un <c>Actualizar</c>/<c>Eliminar</c> algún día, este
    /// test lo detecta sin depender de que quien lo añada recuerde este
    /// comentario.
    /// </summary>
    [Fact]
    public void No_expone_ningun_metodo_publico_que_mute_el_registro_tras_construirlo()
    {
        var metodosMutadores = typeof(RegistroAccesoDocumentoSensible)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // descarta getters de propiedades
            .ToList();

        metodosMutadores.Should().BeEmpty(
            "un rastro que se puede editar o borrar por vía ordinaria no es un rastro (DEC-36)");
    }
}
