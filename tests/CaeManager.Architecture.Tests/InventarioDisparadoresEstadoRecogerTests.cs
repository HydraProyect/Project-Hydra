using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.EstadoMaterializado;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <see cref="InventarioDisparadoresEstado.Recoger"/> sin base de datos (PR 4b del plan del estado materializado): da todas
/// las raíces de la acción, también las que dejan de estar relacionadas. Un cambio da el valor original además del actual
/// (Asignación que cambia de Centro, Trabajador que cambia de Empresa, documento que cambia de titular) y un borrado da las
/// del valor que tenía. Lo que no cuenta (otra columna de los parámetros, un Proyecto que no es evento) no da nada.
///
/// <para>
/// El modelo EF se construye sin abrir conexión, como en <see cref="RestriccionXorDocumentoDeclaradaEnElModeloTests"/>; las
/// entidades se adjuntan como ya guardadas y se cambian por el <c>ChangeTracker</c>, que es lo que ve el detector.
/// </para>
/// </summary>
public class InventarioDisparadoresEstadoRecogerTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 24);

    [Fact]
    public void Una_Asignacion_que_cambia_de_Centro_da_el_Centro_anterior_y_el_nuevo()
    {
        using var contexto = CrearContextoSinConexion();
        var (trabajador, centroAnterior, centroNuevo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var entrada = Adjuntar(contexto, new Asignacion(trabajador, centroAnterior, Hoy));
        entrada.Property(nameof(Asignacion.CentroId)).CurrentValue = centroNuevo;

        var c = Recoger(entrada);

        c.Trabajadores.Should().BeEquivalentTo([trabajador]);
        c.CentrosAfectados.Should().BeEquivalentTo([centroNuevo, centroAnterior]);
        c.Centros.Should().BeEmpty();
    }

    [Fact]
    public void Una_Asignacion_borrada_da_su_Trabajador_y_su_Centro()
    {
        using var contexto = CrearContextoSinConexion();
        var (trabajador, centro) = (Guid.NewGuid(), Guid.NewGuid());
        var entrada = Adjuntar(contexto, new Asignacion(trabajador, centro, Hoy));
        entrada.State = EntityState.Deleted;

        var c = Recoger(entrada);

        c.Trabajadores.Should().BeEquivalentTo([trabajador]);
        c.CentrosAfectados.Should().BeEquivalentTo([centro]);
    }

    [Fact]
    public void Un_Trabajador_que_cambia_de_Empresa_da_la_Empresa_anterior_y_la_nueva()
    {
        using var contexto = CrearContextoSinConexion();
        var (empresaAnterior, empresaNueva) = (Guid.NewGuid(), Guid.NewGuid());
        var trabajador = Trabajador.DeEmpresa(empresaAnterior, "Ana", "Prueba", "12345678Z");
        var entrada = Adjuntar(contexto, trabajador);
        entrada.Property(nameof(Trabajador.EmpresaId)).CurrentValue = empresaNueva;

        var c = Recoger(entrada);

        c.Trabajadores.Should().BeEquivalentTo([trabajador.Id]);
        c.Empresas.Should().BeEquivalentTo([empresaNueva, empresaAnterior]);
    }

    [Fact]
    public void Un_Trabajador_borrado_da_el_Trabajador_y_la_Empresa_que_tenia()
    {
        using var contexto = CrearContextoSinConexion();
        var empresa = Guid.NewGuid();
        var trabajador = Trabajador.DeEmpresa(empresa, "Ana", "Prueba", "12345678Z");
        var entrada = Adjuntar(contexto, trabajador);
        entrada.State = EntityState.Deleted;

        var c = Recoger(entrada);

        c.Trabajadores.Should().BeEquivalentTo([trabajador.Id]);
        c.Empresas.Should().BeEquivalentTo([empresa]);
    }

    [Fact]
    public void Un_documento_que_cambia_de_titular_da_el_titular_anterior_y_el_nuevo()
    {
        using var contexto = CrearContextoSinConexion();
        var (trabajador, empresa) = (Guid.NewGuid(), Guid.NewGuid());
        var entrada = Adjuntar(contexto, Documento.DeTrabajador(trabajador, Guid.NewGuid(), Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1))));
        entrada.Property(nameof(Documento.TrabajadorId)).CurrentValue = null;
        entrada.Property(nameof(Documento.EmpresaId)).CurrentValue = empresa;

        var c = Recoger(entrada);

        c.Trabajadores.Should().BeEquivalentTo([trabajador], "el titular anterior deja de tener el documento");
        c.Empresas.Should().BeEquivalentTo([empresa]);
    }

    [Fact]
    public void Un_documento_borrado_da_el_titular_que_tenia()
    {
        using var contexto = CrearContextoSinConexion();
        var trabajador = Guid.NewGuid();
        var entrada = Adjuntar(contexto, Documento.DeTrabajador(trabajador, Guid.NewGuid(), Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1))));
        entrada.State = EntityState.Deleted;

        var c = Recoger(entrada);

        c.Trabajadores.Should().BeEquivalentTo([trabajador]);
        c.Empresas.Should().BeEmpty();
    }

    [Fact]
    public void Los_umbrales_mandan_el_Tenant_entero_a_la_cola_y_otra_columna_de_los_parametros_no_dispara()
    {
        using var contexto = CrearContextoSinConexion();
        var otraColumna = Adjuntar(contexto, new ParametroSistema(30, 7));
        otraColumna.Property(nameof(ParametroSistema.HorasAvisoVisita)).CurrentValue = 72;

        var cambios = new CambiosEstadoDetectados();
        InventarioDisparadoresEstado.Recoger(otraColumna, cambios).Should().BeFalse();
        cambios.EstaVacio.Should().BeTrue();

        otraColumna.Property(nameof(ParametroSistema.UmbralAmbarDias)).CurrentValue = 45;
        Recoger(otraColumna).TenantEntero.Should().Be(nameof(ParametroSistema));
    }

    [Fact]
    public void Un_Proyecto_que_no_es_evento_no_dispara_y_uno_que_pasa_a_evento_si()
    {
        using var contexto = CrearContextoSinConexion();
        var centro = Guid.NewGuid();
        var obra = Proyecto.Crear(Guid.NewGuid(), centro, "Obra de prueba", Hoy, Hoy.AddMonths(3), null);
        var entrada = Adjuntar(contexto, obra);
        entrada.Property(nameof(Proyecto.Nombre)).CurrentValue = "Obra renombrada";

        var cambios = new CambiosEstadoDetectados();
        InventarioDisparadoresEstado.Recoger(entrada, cambios).Should().BeFalse("una obra no es entrada del cálculo");
        cambios.EstaVacio.Should().BeTrue();

        entrada.Property(nameof(Proyecto.Tipo)).CurrentValue = TipoProyecto.Evento;
        var c = Recoger(entrada);
        c.Eventos.Should().BeEquivalentTo([obra.Id]);
        c.Centros.Should().BeEquivalentTo([centro]);
    }

    [Fact]
    public void Una_entidad_sin_cambios_o_que_no_es_entrada_no_da_nada()
    {
        using var contexto = CrearContextoSinConexion();
        var entrada = Adjuntar(contexto, new Asignacion(Guid.NewGuid(), Guid.NewGuid(), Hoy));

        var cambios = new CambiosEstadoDetectados();
        InventarioDisparadoresEstado.Recoger(entrada, cambios).Should().BeFalse("sin cambios no hay nada que recalcular");
        cambios.EstaVacio.Should().BeTrue();
    }

    /// <summary>La entidad como ya guardada (Unchanged, con su Id), para que el cambio deje el valor original.</summary>
    private static EntityEntry Adjuntar(CaeManagerDbContext contexto, object entidad)
    {
        var entrada = contexto.Entry(entidad);
        if (entrada.Property("Id").CurrentValue is Guid id && id == Guid.Empty)
            entrada.Property("Id").CurrentValue = Guid.NewGuid();
        entrada.State = EntityState.Unchanged;
        return entrada;
    }

    private static CambiosEstadoDeTenant Recoger(EntityEntry entrada)
    {
        var cambios = new CambiosEstadoDetectados();
        InventarioDisparadoresEstado.Recoger(entrada, cambios).Should().BeTrue();
        return cambios.PorTenant.Values.Should().ContainSingle().Subject;
    }

    private static CaeManagerDbContext CrearContextoSinConexion()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=solo-para-construir-el-modelo;Username=x;Password=x")
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
