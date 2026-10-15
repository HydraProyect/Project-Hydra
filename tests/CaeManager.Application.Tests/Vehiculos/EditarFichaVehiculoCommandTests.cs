using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Vehiculos.Commands.EditarFichaVehiculo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Vehiculos;

public class EditarFichaVehiculoCommandTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static void Sellar(EntidadConTenant entidad, Guid tenant) =>
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(entidad, tenant);
    private static Trabajador Persona(string nombre, Guid? tenant = null)
    {
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), nombre, "Ortega", "12345678Z");
        Sellar(trabajador, tenant ?? Tenant);
        return trabajador;
    }
    private static Vehiculo Crear()
    {
        var vehiculo = Vehiculo.DeEmpresa(Guid.NewGuid(), "Camión", "Daily", "9012 GHI");
        Sellar(vehiculo, Tenant);
        vehiculo.FijarNotaInterna("Nota del equipo.");
        return vehiculo;
    }
    private static EditarFichaVehiculoCommand Comando(Vehiculo v, params Guid[] ids) =>
        new(v.Id, v.Version, "Grúa", "Renting", "Última el 11/11/2025", "Registrada", "Nostromo", "AU-1", "Pagado", ids);
    private static (EditarFichaVehiculoCommandHandler Handler, UnitOfWorkFalso Uow) Montar(
        Vehiculo vehiculo, IEnumerable<Trabajador> personas, AlcanceDatosServiceFalso? alcance = null)
    {
        var repo = new VehiculoRepositorioFalso(); repo.Agregar(vehiculo);
        var trabajadores = new TrabajadoresQueryContextFalso(); trabajadores.ListaTrabajadores.AddRange(personas);
        var uow = new UnitOfWorkFalso();
        return (new EditarFichaVehiculoCommandHandler(repo, trabajadores, alcance ?? new(), uow), uow);
    }

    [Fact]
    public async Task Guarda_la_ficha_y_sustituye_solo_las_relaciones_seleccionables()
    {
        var v = Crear(); var quitar = Persona("Raúl"); var anadir = Persona("Hugo");
        v.AutorizarConductor(quitar.Id);
        var (handler, uow) = Montar(v, [quitar, anadir]);
        var resultado = await handler.Handle(Comando(v, anadir.Id), CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue();
        v.Conductores.Select(c => c.TrabajadorId).Should().Equal(anadir.Id);
        v.Tipo.Should().Be("Grúa"); v.Recibo.Should().Be("Pagado");
        v.Nombre.Should().Be("Camión"); v.Notas.Should().Be("Nota del equipo.");
        uow.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Guardar_sin_cambios_no_borra_relaciones_fuera_de_cartera_o_eliminadas()
    {
        var v = Crear(); var visible = Persona("Raúl"); var oculto = Persona("Hugo"); var eliminado = Persona("Luis");
        eliminado.MarcarComoEliminado(Guid.NewGuid());
        foreach (var t in new[] { visible, oculto, eliminado }) v.AutorizarConductor(t.Id);
        var (handler, _) = Montar(v, [visible, oculto, eliminado], new(trabajadorIdsVisibles: [visible.Id, eliminado.Id]));
        var version = v.Version;
        var resultado = await handler.Handle(Comando(v, visible.Id), CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue();
        v.Conductores.Select(c => c.TrabajadorId).Should().BeEquivalentTo([visible.Id, oculto.Id, eliminado.Id]);
        v.Version.Should().Be(version, "el orden o la ausencia de relaciones no visibles no causa bajas");
    }

    [Theory]
    [InlineData("otro-tenant")]
    [InlineData("fuera-cartera")]
    [InlineData("eliminado")]
    [InlineData("inexistente")]
    public async Task Rechaza_una_persona_no_seleccionable_antes_de_modificar_la_ficha(string caso)
    {
        var v = Crear(); var t = Persona("Raúl", caso == "otro-tenant" ? Guid.NewGuid() : Tenant);
        if (caso == "eliminado") t.MarcarComoEliminado(Guid.NewGuid());
        var (handler, uow) = Montar(v, caso == "inexistente" ? [] : [t],
            new(trabajadorIdsVisibles: caso == "fuera-cartera" ? [] : null));
        var resultado = await handler.Handle(Comando(v, t.Id), CancellationToken.None);
        resultado.Error.Codigo.Should().Be("Vehiculo.TrabajadorNoEncontrado");
        v.Tipo.Should().BeNull(); v.Conductores.Should().BeEmpty(); uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_version_antigua_no_guarda_ni_modifica_las_relaciones()
    {
        var v = Crear(); var (handler, uow) = Montar(v, []);
        var resultado = await handler.Handle(Comando(v) with { Version = Guid.NewGuid() }, CancellationToken.None);
        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        v.Tipo.Should().BeNull(); uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_vehiculo_fuera_de_cartera_responde_no_encontrado()
    {
        var v = Crear(); var (handler, uow) = Montar(v, [], new(vehiculoIdsVisibles: []));
        var resultado = await handler.Handle(Comando(v), CancellationToken.None);
        resultado.Error.Codigo.Should().Be("Vehiculo.NoEncontrado"); uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void Valida_los_siete_limites_la_version_y_la_seleccion()
    {
        var comando = Comando(Crear()); var validator = new EditarFichaVehiculoCommandValidator();
        validator.Validate(comando).IsValid.Should().BeTrue();
        var largo = new string('x', Vehiculo.LongitudMaximaCampoFicha + 1);
        var errores = validator.Validate(comando with
        {
            Tipo = largo,
            Titularidad = largo,
            RevisionGrua = largo,
            DeclaracionCe = largo,
            Aseguradora = largo,
            Poliza = largo,
            Recibo = largo,
            Version = Guid.Empty,
            TrabajadorIds = [Guid.Empty, Guid.Empty]
        });
        errores.Errors.Select(e => e.PropertyName).Should().Contain(
            ["Tipo", "Titularidad", "RevisionGrua", "DeclaracionCe", "Aseguradora", "Poliza", "Recibo", "Version", "TrabajadorIds"]);
    }
}
