using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// La conciliación de la regla A-1 sobre lo ya guardado (<see cref="ConciliacionAcreditacionesEmpresaService"/>), con
/// la regla de alta real (<see cref="MundoAcreditaciones"/>): qué crea, qué no toca y que repetirla no duplica.
/// </summary>
public class ConciliacionAcreditacionesEmpresaServiceTests
{
    /// <summary>Cada guardado pasa lo agregado a «lo que ya está en la base», como haría SaveChanges.</summary>
    private sealed class UnitOfWorkDelMundo(MundoAcreditaciones mundo) : IUnitOfWork
    {
        public int VecesGuardado { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var guardadas = mundo.Acreditaciones.Acreditaciones.Count;
            mundo.Guardar();
            VecesGuardado++;
            return Task.FromResult(guardadas);
        }
    }

    private static ConciliacionAcreditacionesEmpresaService Conciliacion(MundoAcreditaciones mundo, IUnitOfWork unitOfWork) =>
        new(mundo.CentrosContexto, mundo.Servicio(), unitOfWork);

    [Fact]
    public async Task Crea_la_acreditacion_de_un_documento_de_empresa_ya_guardado_en_un_centro_de_plataforma_sin_trabajadores()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var deEmpresa = mundo.DocumentoDeEmpresa(tipo);
        var acceso = mundo.AccesoPlataforma(mundo.Centro("Planta sin personal", mundo.EmpresaId));
        mundo.AccesoCorreo(mundo.Centro("Planta por correo", mundo.EmpresaId));
        var unitOfWork = new UnitOfWorkDelMundo(mundo);

        var creadas = await Conciliacion(mundo, unitOfWork).ConciliarTenantEnCursoAsync();

        creadas.Should().Be(1);
        unitOfWork.VecesGuardado.Should().Be(1);
        mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma
            .Select(a => (a.DocumentoId, a.CanalGestionDocumentalId))
            .Should().BeEquivalentTo([(deEmpresa.Id, acceso.Id)], "el correo nunca acredita");
    }

    [Fact]
    public async Task Repetirla_no_crea_nada_ni_guarda()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        mundo.DocumentoDeEmpresa(tipo);
        mundo.AccesoPlataforma(mundo.Centro("Planta sin personal", mundo.EmpresaId));
        var unitOfWork = new UnitOfWorkDelMundo(mundo);
        await Conciliacion(mundo, unitOfWork).ConciliarTenantEnCursoAsync();

        var segunda = await Conciliacion(mundo, unitOfWork).ConciliarTenantEnCursoAsync();

        segunda.Should().Be(0);
        unitOfWork.VecesGuardado.Should().Be(1, "una pasada sin nada que crear no guarda");
        mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Should().ContainSingle();
    }

    [Fact]
    public async Task No_toca_los_documentos_de_trabajador()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        var centro = mundo.Centro("Planta", mundo.EmpresaId);
        mundo.AccesoPlataforma(centro);
        var trabajador = mundo.Trabajador();
        mundo.Asignacion(trabajador, centro);
        mundo.DocumentoDe(trabajador, tipo);
        var unitOfWork = new UnitOfWorkDelMundo(mundo);

        var creadas = await Conciliacion(mundo, unitOfWork).ConciliarTenantEnCursoAsync();

        creadas.Should().Be(0);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Recorre_todos_los_centros_de_plataforma_en_lotes_y_guarda_cada_lote()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        var deEmpresa = mundo.DocumentoDeEmpresa(tipo);
        var centros = Enumerable.Range(0, ConciliacionAcreditacionesEmpresaService.CentrosPorLote + 1)
            .Select(i => mundo.Centro($"Planta {i}", mundo.EmpresaId))
            .ToList();
        var accesos = centros.Select(mundo.AccesoPlataforma).ToList();
        var unitOfWork = new UnitOfWorkDelMundo(mundo);

        var creadas = await Conciliacion(mundo, unitOfWork).ConciliarTenantEnCursoAsync();

        creadas.Should().Be(centros.Count);
        unitOfWork.VecesGuardado.Should().Be(2, "un lote lleno y otro con el Centro que sobra");
        mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma
            .Select(a => (a.DocumentoId, a.CanalGestionDocumentalId))
            .Should().BeEquivalentTo(accesos.Select(a => (deEmpresa.Id, a.Id)));
    }

    [Fact]
    public async Task Un_centro_sin_gestion_cae_queda_fuera_igual_que_en_el_alta()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC");
        mundo.DocumentoDeEmpresa(tipo);
        var centro = mundo.Centro("Almacén sin gestión", mundo.EmpresaId);
        mundo.AccesoPlataforma(centro);
        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);

        var creadas = await Conciliacion(mundo, new UnitOfWorkDelMundo(mundo)).ConciliarTenantEnCursoAsync();

        creadas.Should().Be(0);
    }
}
