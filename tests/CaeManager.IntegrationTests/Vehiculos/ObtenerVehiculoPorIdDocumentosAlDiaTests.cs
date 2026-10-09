using CaeManager.Application.Documentos;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Vehiculos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Vehiculos;

/// <summary>
/// El detalle de un Vehículo trae lo que pinta el anillo y la banda de su ficha 360: cuántos de sus
/// documentos REGISTRADOS están al día y cuál es su peor estado. Contra PostgreSQL real, porque la
/// propiedad que importa —que un documento sustituido no cuente y que el estado salga de la misma
/// calculadora que la lista de documentos— vive en la consulta.
/// </summary>
public class ObtenerVehiculoPorIdDocumentosAlDiaTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();

    private Guid _camionGrua;
    private Guid _recienCreado;
    private Guid _todoAlDia;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarDias, UmbralRojoDias));

        var empresa = new Empresa("Montajes Skynet S.L.");
        contexto.Empresas.Add(empresa);

        static TipoDocumento Tipo(string nombre) => new(nombre, 12, true, 1, AmbitoAplicacion.Vehiculo, RequisitoDocumental.Si);
        var itv = Tipo("ITV");
        var seguro = Tipo("Seguro");
        var fichaTecnica = Tipo("Ficha técnica");
        var permiso = Tipo("Permiso de circulación");
        contexto.TiposDocumento.AddRange(itv, seguro, fichaTecnica, permiso);
        await contexto.SaveChangesAsync();

        var camionGrua = Vehiculo.DeEmpresa(empresa.Id, "Camión grúa", "Iveco Daily", "9012 GHI");
        var recienCreado = Vehiculo.DeEmpresa(empresa.Id, "Furgoneta nueva", "Transit", "0001 AAA");
        var todoAlDia = Vehiculo.DeEmpresa(empresa.Id, "Furgoneta T2", "Transit", "0002 BBB");
        contexto.Vehiculos.AddRange(camionGrua, recienCreado, todoAlDia);
        await contexto.SaveChangesAsync();
        (_camionGrua, _recienCreado, _todoAlDia) = (camionGrua.Id, recienCreado.Id, todoAlDia.Id);

        // El Camión grúa de la maqueta: ITV vencida, seguro por vencer, ficha técnica sin confirmar y
        // permiso de circulación que no caduca. 2 de 4 al día.
        var itvAnterior = Documento.DeVehiculo(camionGrua.Id, itv.Id, _hoy.AddDays(-800), VigenciaDocumento.VenceEl(_hoy.AddDays(-400)));
        var itvVencida = Documento.DeVehiculo(camionGrua.Id, itv.Id, _hoy.AddDays(-400), VigenciaDocumento.VenceEl(_hoy.AddDays(-36)));
        contexto.Documentos.AddRange(
            itvAnterior,
            itvVencida,
            Documento.DeVehiculo(camionGrua.Id, seguro.Id, _hoy.AddDays(-350), VigenciaDocumento.VenceEl(_hoy.AddDays(11))),
            Documento.DeVehiculo(camionGrua.Id, fichaTecnica.Id, _hoy.AddDays(-350), VigenciaDocumento.SinConfirmar),
            Documento.DeVehiculo(camionGrua.Id, permiso.Id, _hoy.AddDays(-350), VigenciaDocumento.NoCaduca));

        contexto.Documentos.AddRange(
            Documento.DeVehiculo(todoAlDia.Id, itv.Id, _hoy.AddDays(-10), VigenciaDocumento.VenceEl(_hoy.AddDays(UmbralAmbarDias + 60))),
            Documento.DeVehiculo(todoAlDia.Id, permiso.Id, _hoy.AddDays(-10), VigenciaDocumento.NoCaduca));
        await contexto.SaveChangesAsync();

        // La ITV anterior quedó sustituida por la que hoy está vencida: es historial y no cuenta.
        itvAnterior.SustituirPor(itvVencida, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Cuenta_al_dia_los_documentos_registrados_vigentes_y_por_vencer_y_da_el_peor_estado()
    {
        var detalle = await ObtenerAsync(_camionGrua);

        detalle.Should().NotBeNull();
        detalle!.DocumentosAlDia.Should().Be(new FraccionCumplimiento(AlDia: 2, Requeridos: 4),
            "vencido y sin confirmar no cuentan; el sustituido es historial y no entra en el total");
        detalle.DocumentosAlDia.Porcentaje.Should().Be(50);
        detalle.PeorEstadoDocumental.Should().Be(EstadoDocumento.Vencido);
    }

    [Fact]
    public async Task Sin_documentos_no_hay_fraccion_ni_peor_estado()
    {
        var detalle = await ObtenerAsync(_recienCreado);

        detalle.Should().NotBeNull();
        detalle!.DocumentosAlDia.Should().Be(FraccionCumplimiento.SinRequisitos);
        detalle.DocumentosAlDia.Porcentaje.Should().BeNull();
        detalle.PeorEstadoDocumental.Should().BeNull();
    }

    [Fact]
    public async Task Con_todo_al_dia_la_fraccion_es_completa_y_el_peor_estado_es_el_del_documento_con_fecha()
    {
        var detalle = await ObtenerAsync(_todoAlDia);

        detalle!.DocumentosAlDia.Should().Be(new FraccionCumplimiento(AlDia: 2, Requeridos: 2));
        detalle.PeorEstadoDocumental.Should().Be(EstadoDocumento.Vigente);
    }

    [Fact]
    public async Task Fuera_del_alcance_del_usuario_no_devuelve_nada_tampoco_la_documentacion()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerVehiculoPorIdQueryHandler(
            contexto, contexto, new AlcanceDatosServiceFalso(vehiculoIds: [_todoAlDia]),
            new CalculoEstadoDocumentalService(contexto, contexto, contexto));

        var detalle = await handler.Handle(new ObtenerVehiculoPorIdQuery(_camionGrua), CancellationToken.None);

        detalle.Should().BeNull("no encontrado y sin acceso no se distinguen");
    }

    private async Task<VehiculoDetalleDto?> ObtenerAsync(Guid vehiculoId)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerVehiculoPorIdQueryHandler(
            contexto, contexto, new AlcanceDatosServiceFalso(), new CalculoEstadoDocumentalService(contexto, contexto, contexto));

        return await handler.Handle(new ObtenerVehiculoPorIdQuery(vehiculoId), CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var builder = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual));

        return new CaeManagerDbContext(builder.Options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
