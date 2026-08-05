using CaeManager.Application.Documentos;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Ordenacion;

/// <summary>
/// Trabajador, Empresa y Vehículo no tienen estado propio en el modelo — a
/// diferencia de Centro. Su "estado documental" se deriva del peor estado de
/// vigencia de sus Documentos, y es lo que alimenta el filtro de esas tres
/// pantallas.
///
/// Lo que se protege aquí es que "peor" signifique lo mismo que en el resto
/// del sistema (<see cref="CalculadoraEstadoDocumento"/>, fuente única de
/// verdad) y que un propietario sin documentos no se confunda con uno cuya
/// documentación está en regla — que es justo el error que haría al gestor
/// pasar por alto a quien no ha entregado nada.
/// </summary>
public class EstadoDocumentalDerivadoTests : IAsyncLifetime
{
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    private Guid _conVencido;
    private Guid _soloVigentes;
    private Guid _sinDocumentos;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var parametros = await contexto.ParametrosSistema.SingleOrDefaultAsync();
        if (parametros is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarDias, UmbralRojoDias));
        else
            parametros.Actualizar(UmbralAmbarDias, UmbralRojoDias);

        var empresa = new Empresa("Contratas del Sur S.L.", "B12345674");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var conVencido = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
        var soloVigentes = Trabajador.DeEmpresa(empresa.Id, "Luis", "Pérez", "12345678Z");
        var sinDocumentos = Trabajador.DeEmpresa(empresa.Id, "Marta", "Ruiz", "00000000T");
        contexto.Trabajadores.AddRange(conVencido, soloVigentes, sinDocumentos);

        var tipo = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador, true);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        _conVencido = conVencido.Id;
        _soloVigentes = soloVigentes.Id;
        _sinDocumentos = sinDocumentos.Id;

        contexto.Documentos.AddRange(
            // El peor de los dos manda: no basta con tener uno en regla.
            Documento.DeTrabajador(conVencido.Id, tipo.Id, _hoy.AddDays(-400), _hoy.AddDays(-1)),
            Documento.DeTrabajador(conVencido.Id, tipo.Id, _hoy.AddDays(-10), _hoy.AddDays(UmbralAmbarDias + 60)),
            Documento.DeTrabajador(soloVigentes.Id, tipo.Id, _hoy.AddDays(-10), _hoy.AddDays(UmbralAmbarDias + 60)));

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_estado_derivado_es_el_peor_de_los_documentos_del_propietario()
    {
        await using var contexto = CrearContexto();
        var servicio = new CalculoEstadoDocumentalService(contexto, contexto);

        var estados = await servicio.CalcularPeorEstadoAsync(
            AmbitoAplicacion.Trabajador, [_conVencido, _soloVigentes, _sinDocumentos], CancellationToken.None);

        estados[_conVencido].Should().Be(EstadoDocumento.Vencido);
        estados[_soloVigentes].Should().Be(EstadoDocumento.Vigente);
    }

    [Fact]
    public async Task Un_propietario_sin_documentos_no_aparece_y_no_se_confunde_con_uno_al_dia()
    {
        await using var contexto = CrearContexto();
        var servicio = new CalculoEstadoDocumentalService(contexto, contexto);

        var estados = await servicio.CalcularPeorEstadoAsync(
            AmbitoAplicacion.Trabajador, [_conVencido, _soloVigentes, _sinDocumentos], CancellationToken.None);

        estados.ContainsKey(_sinDocumentos).Should().BeFalse();

        // Y el filtro lo trata como su propio caso, no como "vigente".
        EstadoDocumentalFiltro.Coincide(null, EstadoDocumentalFiltro.SinDocumentos).Should().BeTrue();
        EstadoDocumentalFiltro.Coincide(null, nameof(EstadoDocumento.Vigente)).Should().BeFalse();
    }

    [Fact]
    public async Task Sin_propietarios_no_consulta_nada_y_devuelve_vacio()
    {
        await using var contexto = CrearContexto();
        var servicio = new CalculoEstadoDocumentalService(contexto, contexto);

        var estados = await servicio.CalcularPeorEstadoAsync(
            AmbitoAplicacion.Trabajador, [], CancellationToken.None);

        estados.Should().BeEmpty();
    }

    [Fact]
    public void El_orden_del_filtro_pone_primero_lo_que_mas_urge()
    {
        var claves = new EstadoDocumento?[]
        {
            EstadoDocumento.Vencido, EstadoDocumento.Urgente, EstadoDocumento.Proximo,
            EstadoDocumento.Vigente, EstadoDocumento.NoAplica, null
        }.Select(EstadoDocumentalFiltro.ClaveOrden);

        claves.Should().BeInAscendingOrder();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
