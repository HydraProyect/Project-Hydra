using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <b>La siembra de cobertura deja de verdad un ejemplar de cada estado que declara.</b> La matriz declarada
/// (<see cref="CoberturaPantallasMatriz"/>) se compara con lo MEDIDO sobre PostgreSQL real bajo RLS efectiva: los estados de
/// documento salen de las mismas reglas que usa la aplicación (<see cref="CalculadoraEstadoDocumento"/> y
/// <see cref="ReglaBloqueoDeAcceso"/>) y los de Centro, del servicio que los calcula, no de la tabla del sembrador. Una
/// casilla declarada sin medir, medida sin declarar o por debajo de su mínimo deja el test en rojo.
/// </summary>
public sealed class CoberturaPantallasFixture : IAsyncLifetime
{
    internal ArnesDeArranqueRuntime Arnes { get; private set; } = null!;
    internal IConfiguration Configuracion { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        Configuracion = new ConfigurationBuilder()
            .AddConfiguration(Arnes.Servicios.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?> { [CoberturaPantallasSeeder.ClaveConfiguracion] = "true" })
            .Build();

        await SembrarAsync();
    }

    internal async Task SembrarAsync()
    {
        using var ambito = Arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        await CoberturaPantallasSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
            Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
    }

    public Task DisposeAsync() => Arnes.DisposeAsync().AsTask();
}

public class CoberturaPantallasTests(CoberturaPantallasFixture fixture) : IClassFixture<CoberturaPantallasFixture>
{
    private static readonly string[] TiposDeEmpresa =
    [
        CoberturaPantallasSeeder.Rnt, CoberturaPantallasSeeder.Rlc, CoberturaPantallasSeeder.CorrienteSeguridadSocial,
        CoberturaPantallasSeeder.CorrienteHacienda, CoberturaPantallasSeeder.SeguroResponsabilidadCivil,
        CoberturaPantallasSeeder.EvaluacionDeRiesgos, CoberturaPantallasSeeder.PlanificacionPreventiva
    ];

    private static readonly string[] TiposDeTrabajador = [CoberturaPantallasSeeder.AptitudMedica, CoberturaPantallasSeeder.EntregaEpi];

    [Fact]
    public async Task La_matriz_declarada_es_exactamente_la_medida_y_cada_casilla_llega_a_su_minimo()
    {
        var medido = new Dictionary<string, int>();
        foreach (var (clave, valor) in await MedirAsync(CoberturaPantallasSeeder.NombreTenantAlfa, MedirAlfaAsync))
            medido[clave] = valor;
        foreach (var (clave, valor) in await MedirAsync(CoberturaPantallasSeeder.NombreTenantBeta, MedirBetaAsync))
            medido[clave] = valor;
        foreach (var (clave, valor) in await MedirUsuariosAsync())
            medido[clave] = valor;

        var declarado = CoberturaPantallasMatriz.Celdas.ToDictionary(c => c.Clave);
        medido.Keys.Should().BeEquivalentTo(declarado.Keys, "toda casilla medida está declarada y toda declarada se mide");

        foreach (var celda in CoberturaPantallasMatriz.Celdas)
            medido[celda.Clave].Should().BeGreaterThanOrEqualTo(celda.Minimo, $"«{celda.Clave}» tiene que llegar a su mínimo declarado");
    }

    [Fact]
    public async Task Ningun_correo_sembrado_es_entregable()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var tenants = await bootstrap.Tenants.Where(t => CoberturaPantallasSeeder.NombresTenants.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
        tenants.Should().HaveCount(3);

        (await bootstrap.Users.IgnoreQueryFilters().Where(u => tenants.Contains(u.TenantId)).Select(u => u.Email!).ToListAsync())
            .Should().NotBeEmpty().And.OnlyContain(e => e.EndsWith("@caemanager.local"));

        var contactos = await bootstrap.ContactosAgenda.IgnoreQueryFilters().Where(c => tenants.Contains(c.TenantId)).Select(c => c.Email).ToListAsync();
        contactos.Should().OnlyContain(e => e != null && e.EndsWith(".caemanager.local"));

        var reclamacion = await bootstrap.DelegacionesEmpresa.IgnoreQueryFilters()
            .Where(d => tenants.Contains(d.TenantId) && d.ContactoReclamacionCorreo != null).Select(d => d.ContactoReclamacionCorreo!).ToListAsync();
        reclamacion.Should().OnlyContain(e => e.EndsWith("@caemanager.local"));
    }

    [Fact]
    public async Task Una_segunda_siembra_no_escribe_nada()
    {
        var antes = await ContarAsync();
        await fixture.SembrarAsync();
        (await ContarAsync()).Should().BeEquivalentTo(antes);
    }

    private async Task<Dictionary<string, int>> ContarAsync()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var tenants = await bootstrap.Tenants.Where(t => CoberturaPantallasSeeder.NombresTenants.Contains(t.Nombre)).Select(t => t.Id).ToListAsync();
        return new Dictionary<string, int>
        {
            ["Tenants"] = tenants.Count,
            ["Empresas"] = await bootstrap.Empresas.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Trabajadores"] = await bootstrap.Trabajadores.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Documentos"] = await bootstrap.Documentos.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Conversaciones"] = await bootstrap.Conversaciones.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Visitas"] = await bootstrap.Visitas.IgnoreQueryFilters().CountAsync(e => tenants.Contains(e.TenantId)),
            ["Usuarios"] = await bootstrap.Users.IgnoreQueryFilters().CountAsync(u => tenants.Contains(u.TenantId))
        };
    }

    private async Task<Dictionary<string, int>> MedirAlfaAsync(CaeManagerDbContext contexto)
    {
        var hoy = DiaDeNegocio.Hoy();
        var resultado = new Dictionary<string, int>();
        void Sumar(string dimension, string caso, int n = 1)
        {
            var clave = new CeldaDeCobertura(dimension, caso, 0).Clave;
            resultado[clave] = resultado.GetValueOrDefault(clave) + n;
        }

        // Todas las casillas de documento existen en el resultado aunque valgan 0.
        foreach (var celda in CoberturaPantallasMatriz.Celdas.Where(c => c.Dimension.StartsWith("Documento de ")))
            resultado[celda.Clave] = 0;

        var parametros = await contexto.ParametrosSistema.SingleAsync();
        var centros = await contexto.Centros.ToDictionaryAsync(c => c.Nombre);
        var principal = centros[CoberturaPantallasSeeder.CentroPrincipal];
        var tipos = await contexto.TiposDocumento.ToDictionaryAsync(t => t.Nombre);
        var filas = await contexto.TiposDocumentoCentros.Where(f => f.CentroId == principal.Id).ToDictionaryAsync(f => f.TipoDocumentoId);
        var operativos = await contexto.Documentos.Operativos().ToListAsync();

        string Estado(IReadOnlyList<Documento> documentos, TipoDocumentoCentro fila)
        {
            if (documentos.Count == 0)
                return "Pendiente";

            var condiciones = new CondicionesDeAccesoDelCentro(
                fila.PeriodicidadEspecialMeses, ReglaBloqueoDeAcceso.ResolverToleranciaDias(fila.ToleranciaDias, null));
            var paraAcceso = documentos.Select(d => new DocumentoParaAcceso(
                VigenciaDocumento.Rehidratar(d.EstadoVigencia, d.FechaVencimiento), d.FechaEmision)).ToList();
            var requisito = ReglaBloqueoDeAcceso.Evaluar(paraAcceso, condiciones, hoy);

            if (fila.BloqueaAcceso && ReglaBloqueoDeAcceso.Bloquea(requisito.Situacion))
                return "Bloqueado";
            if (requisito.EnToleranciaHasta is not null)
                return "En tolerancia";

            var documento = documentos[0];
            return CalculadoraEstadoDocumento.Calcular(
                    documento.EstadoVigencia, documento.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias) switch
            {
                EstadoDocumento.Vencido => "Vencido",
                EstadoDocumento.Urgente or EstadoDocumento.Proximo => "Por vencer",
                EstadoDocumento.SinConfirmar => "Sin confirmar",
                _ => "Vigente"
            };
        }

        // Trabajadores con Asignación activa en el Centro Principal.
        var asignados = await contexto.Asignaciones
            .Where(a => a.CentroId == principal.Id && a.FechaBaja == null).Select(a => a.TrabajadorId).ToListAsync();
        foreach (var trabajadorId in asignados)
            foreach (var nombreTipo in TiposDeTrabajador)
            {
                var tipoId = tipos[nombreTipo].Id;
                Sumar("Documento de Trabajador", Estado(
                    operativos.Where(d => d.TrabajadorId == trabajadorId && d.TipoDocumentoId == tipoId).ToList(), filas[tipoId]));
            }

        // Empresas: la Empresa propia y las dos Subcontratas.
        var empresas = await contexto.Empresas
            .Where(e => e.RazonSocial == CoberturaPantallasSeeder.EmpresaPropiaAlfa
                        || e.RazonSocial == CoberturaPantallasSeeder.SubcontrataConSedes
                        || e.RazonSocial == CoberturaPantallasSeeder.SubcontrataSinContactos)
            .Select(e => e.Id).ToListAsync();
        foreach (var empresaId in empresas)
            foreach (var nombreTipo in TiposDeEmpresa)
            {
                var tipoId = tipos[nombreTipo].Id;
                Sumar("Documento de Empresa", Estado(
                    operativos.Where(d => d.EmpresaId == empresaId && d.TipoDocumentoId == tipoId).ToList(), filas[tipoId]));
            }

        // Vehículos.
        var itc = tipos[CoberturaPantallasSeeder.InspeccionTecnica].Id;
        foreach (var vehiculoId in await contexto.Vehiculos.Select(v => v.Id).ToListAsync())
            Sumar("Documento de Vehículo", Estado(operativos.Where(d => d.VehiculoId == vehiculoId && d.TipoDocumentoId == itc).ToList(), filas[itc]));

        // Centros, con el servicio que calcula el estado y los pares exigidos.
        var servicio = new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto);
        var ids = centros.Values.Select(c => c.Id).ToList();
        var estados = await servicio.CalcularAsync(ids, CancellationToken.None);
        var pares = await servicio.ObtenerParesExigidosAsync(ids, CancellationToken.None);
        int ParesDe(string centro) => pares.Count(p => p.CentroId == centros[centro].Id);

        resultado[new CeldaDeCobertura("Centro", "Bloqueado (plataforma CAE)", 0).Clave] =
            estados[centros[CoberturaPantallasSeeder.CentroBloqueadoPorPlataforma].Id].Estado == EstadoCentro.Bloqueado ? 1 : 0;
        resultado[new CeldaDeCobertura("Centro", "Sin gestión CAE", 0).Clave] =
            estados[centros[CoberturaPantallasSeeder.CentroSinGestionCae].Id].Estado == EstadoCentro.SinGestionCae ? 1 : 0;
        resultado[new CeldaDeCobertura("Centro", "Sin requisitos", 0).Clave] =
            ParesDe(CoberturaPantallasSeeder.CentroSinRequisitos) == 0 && asignados.Count > 0 ? 1 : 0;

        var trabajadoresDeLaEmpresa = await contexto.Trabajadores.CountAsync();
        var conRequisito = pares.Where(p => p.CentroId == centros[CoberturaPantallasSeeder.CentroRequisitosParaAlgunos].Id)
            .Select(p => p.TrabajadorId).Distinct().Count();
        resultado[new CeldaDeCobertura("Centro", "Requisitos solo para algunos Trabajadores", 0).Clave] =
            conRequisito is > 0 and var n && n < trabajadoresDeLaEmpresa ? 1 : 0;

        // Empresas con Delegaciones, Oficios y contactos.
        var delegaciones = await contexto.DelegacionesEmpresa.ToListAsync();
        var oficios = await contexto.OficiosEmpresa.ToListAsync();
        resultado[new CeldaDeCobertura("Empresa", "Con delegaciones y Oficios", 0).Clave] =
            delegaciones.Select(d => d.EmpresaId).Intersect(oficios.Select(o => o.EmpresaId)).Count();
        resultado[new CeldaDeCobertura("Empresa", "Delegación con contacto de reclamación", 0).Clave] =
            delegaciones.Count(d => d.ContactoReclamacionCorreo is not null);
        resultado[new CeldaDeCobertura("Empresa", "Delegación sin contacto de reclamación", 0).Clave] =
            delegaciones.Count(d => d.ContactoReclamacionCorreo is null);
        var conContactos = (await contexto.ContactosAgenda.Where(c => c.EmpresaId != null).Select(c => c.EmpresaId!.Value).ToListAsync()).ToHashSet();
        var sinContactos = await contexto.Empresas
            .Where(e => e.RazonSocial == CoberturaPantallasSeeder.SubcontrataSinContactos).Select(e => e.Id).ToListAsync();
        resultado[new CeldaDeCobertura("Empresa", "Sin contactos", 0).Clave] = sinContactos.Count(id => !conContactos.Contains(id));

        // Comunicaciones.
        var conversaciones = await contexto.Conversaciones.ToListAsync();
        var mensajes = await contexto.Mensajes.ToListAsync();
        var conAdjunto = (await contexto.AdjuntosMensaje.Select(a => a.MensajeId).ToListAsync()).ToHashSet();
        resultado[new CeldaDeCobertura("Comunicaciones", "Conversación con muchos mensajes", 0).Clave] =
            mensajes.GroupBy(m => m.ConversacionId).Count(g => g.Count() >= CoberturaPantallasMatriz.MensajesDeLaConversacionLarga);
        resultado[new CeldaDeCobertura("Comunicaciones", "Mensaje con adjunto", 0).Clave] = mensajes.Count(m => conAdjunto.Contains(m.Id));
        resultado[new CeldaDeCobertura("Comunicaciones", "Remitente conocido", 0).Clave] = conversaciones.Count(c => c.ClienteId is not null);
        resultado[new CeldaDeCobertura("Comunicaciones", "Remitente desconocido", 0).Clave] = conversaciones.Count(c => c.ClienteId is null);
        foreach (var estado in Enum.GetValues<EstadoConversacion>())
            resultado[new CeldaDeCobertura("Comunicaciones", $"Conversación {estado}", 0).Clave] = conversaciones.Count(c => c.Estado == estado);

        // Visitas futuras con quien no tiene Asignación activa en el Centro, y extranjeros del mes.
        var visitas = await contexto.Visitas.Where(v => v.FechaInicio > hoy).ToListAsync();
        var asistentes = await contexto.VisitasTrabajadores.ToListAsync();
        var activas = (await contexto.Asignaciones.Where(a => a.FechaBaja == null).ToListAsync())
            .Select(a => (a.TrabajadorId, a.CentroId)).ToHashSet();
        resultado[new CeldaDeCobertura("Visita", "Futura con Trabajador sin Asignación activa en el Centro", 0).Clave] =
            visitas.Count(v => asistentes.Any(a => a.VisitaId == v.Id && !activas.Contains((a.TrabajadorId, v.CentroId))));
        var delMes = (await contexto.Visitas.ToListAsync()).Where(v => v.FechaInicio.Year == hoy.Year && v.FechaInicio.Month == hoy.Month)
            .Select(v => v.Id).ToHashSet();
        resultado[new CeldaDeCobertura("Visita", "Trabajador extranjero en el mes", 0).Clave] =
            asistentes.Count(a => delMes.Contains(a.VisitaId) && a.EsExtranjeroCobrable);

        resultado[new CeldaDeCobertura("Facturación", "Tarifa fijada", 0).Clave] = await contexto.TarifasCliente.CountAsync();
        return resultado;
    }

    private async Task<Dictionary<string, int>> MedirBetaAsync(CaeManagerDbContext contexto)
    {
        var centro = await contexto.Centros.SingleAsync(c => c.Nombre == CoberturaPantallasSeeder.CentroMasivo);
        var servicio = new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto, contexto);
        var pares = await servicio.ObtenerParesExigidosAsync([centro.Id], CancellationToken.None);
        var tiposDeVolumen = await contexto.TiposDocumento
            .Where(t => t.Nombre.StartsWith(CoberturaPantallasSeeder.PrefijoTipoDeVolumen)).Select(t => t.Id).ToListAsync();

        return new Dictionary<string, int>
        {
            [new CeldaDeCobertura("Volumen", "Trabajadores en un Centro", 0).Clave] =
                await contexto.Asignaciones.CountAsync(a => a.CentroId == centro.Id && a.FechaBaja == null),
            [new CeldaDeCobertura("Volumen", "Documentos pendientes de una Empresa", 0).Clave] =
                pares.Count(p => tiposDeVolumen.Contains(p.TipoDocumentoId) && p.Estado == EstadoDocumento.Faltante)
        };
    }

    /// <summary>Un usuario por rol, y la cartera del Gestor CAE con ella y del que queda sin ella.</summary>
    private async Task<Dictionary<string, int>> MedirUsuariosAsync()
    {
        var resultado = new Dictionary<string, int>();
        async Task<Guid> IdAsync(string tenant, string email, string rol, string caso)
        {
            Guid tenantId, usuarioId;
            await using (var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear())
            {
                tenantId = await bootstrap.Tenants.Where(t => t.Nombre == tenant).Select(t => t.Id).SingleAsync();
                usuarioId = await bootstrap.Users.IgnoreQueryFilters().Where(u => u.Email == email && u.TenantId == tenantId).Select(u => u.Id).SingleAsync();
            }

            using var ambito = fixture.Arnes.Servicios.CreateScope();
            var usuarios = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                var usuario = await usuarios.FindByIdAsync(usuarioId.ToString());
                resultado[new CeldaDeCobertura("Usuario", caso, 0).Clave] = usuario is not null && await usuarios.IsInRoleAsync(usuario, rol) ? 1 : 0;
            }

            return usuarioId;
        }

        await IdAsync(CoberturaPantallasSeeder.NombreTenantAlfa, CoberturaPantallasSeeder.EmailAdministradorAlfa, Roles.Administrador, "Administrador");
        await IdAsync(CoberturaPantallasSeeder.NombreTenantOperador, CoberturaPantallasSeeder.EmailDireccionCae, Roles.DireccionCae, "Dirección CAE");
        await IdAsync(CoberturaPantallasSeeder.NombreTenantOperador, CoberturaPantallasSeeder.EmailCoordinadorCae, Roles.CoordinadorCae, "Coordinador CAE");
        var conCartera = await IdAsync(CoberturaPantallasSeeder.NombreTenantOperador, CoberturaPantallasSeeder.EmailGestorConCartera, Roles.GestorCae, "Gestor CAE con Asignación de Cartera");
        var sinCartera = await IdAsync(CoberturaPantallasSeeder.NombreTenantOperador, CoberturaPantallasSeeder.EmailGestorSinCartera, Roles.GestorCae, "Gestor CAE sin Asignación de Cartera");
        await IdAsync(CoberturaPantallasSeeder.NombreTenantAlfa, CoberturaPantallasSeeder.EmailConsultaAlfa, Roles.Consulta, "Consulta");
        await IdAsync(CoberturaPantallasSeeder.NombreTenantAlfa, CoberturaPantallasSeeder.EmailClienteAlfa, Roles.Cliente, "Cliente");

        // Las carteras no llevan filtro global de Tenant: se leen sin filtro y acotadas al Operador CAE de la siembra.
        await using var contexto = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        var vigentes = await contexto.AsignacionesCartera
            .Where(c => c.Estado == EstadoAsignacion.Vigente).Select(c => c.UsuarioId).ToListAsync();
        var claveConCartera = new CeldaDeCobertura("Usuario", "Gestor CAE con Asignación de Cartera", 0).Clave;
        var claveSinCartera = new CeldaDeCobertura("Usuario", "Gestor CAE sin Asignación de Cartera", 0).Clave;
        resultado[claveConCartera] = resultado[claveConCartera] == 1 && vigentes.Contains(conCartera) ? 1 : 0;
        resultado[claveSinCartera] = resultado[claveSinCartera] == 1 && !vigentes.Contains(sinCartera) ? 1 : 0;
        return resultado;
    }

    private async Task<T> MedirAsync<T>(string tenant, Func<CaeManagerDbContext, Task<T>> medir)
    {
        Guid tenantId;
        await using (var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear())
            tenantId = await bootstrap.Tenants.Where(t => t.Nombre == tenant).Select(t => t.Id).SingleAsync();

        using var ambito = fixture.Arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(tenantId))
            return await medir(contexto);
    }
}

/// <summary>La guarda de <see cref="CoberturaPantallasSeeder"/>, sin base de datos: decide antes de leer o escribir nada.</summary>
public class CoberturaPantallasArranqueTests
{
    [Theory]
    [InlineData("Production", true, true, "*solo puede activarse en Development*")]
    [InlineData("Staging", true, true, "*solo puede activarse en Development*")]
    [InlineData("Development", true, true, null)]
    [InlineData("Production", false, true, null)]
    [InlineData("Production", true, false, null)]
    public void La_guarda_rechaza_todo_entorno_que_no_sea_Development_y_es_inerte_sin_las_claves(
        string entorno, bool datosPrueba, bool cobertura, string? mensajeEsperado)
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = datosPrueba.ToString(),
            [CoberturaPantallasSeeder.ClaveConfiguracion] = cobertura.ToString()
        }).Build();

        var llamada = () => CoberturaPantallasSeeder.RechazarFueraDeDevelopment(configuracion, new EntornoDePrueba(entorno));

        if (mensajeEsperado is null)
            llamada.Should().NotThrow();
        else
            llamada.Should().Throw<InvalidOperationException>().WithMessage(mensajeEsperado);
    }

    [Fact]
    public async Task Fuera_de_Development_la_siembra_lanza_antes_de_escribir_nada()
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatosPrueba:Activo"] = "true",
            [CoberturaPantallasSeeder.ClaveConfiguracion] = "true"
        }).Build();

        var siembra = () => CoberturaPantallasSeeder.SeedAsync(
            dbContext: null!, userManager: null!, configuracion, new EntornoDePrueba("Staging"), NullLogger.Instance);

        await siembra.Should().ThrowAsync<InvalidOperationException>().WithMessage("*solo puede activarse en Development*");
    }
}
