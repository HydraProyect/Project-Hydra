using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionEmpresa;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// S4 (coherencia entre superficies, dirigida por tabla): la ventana de reclamación es UNA regla
/// (<see cref="VentanaReclamacion"/>: con fecha de vencimiento y como mucho dentro de 3 meses, sin límite
/// inferior) y la leen la vista previa del lote de Trabajadores, la del lote de Empresas, la preparación
/// del envío al Cliente empresarial y la del envío a la Empresa (la vista previa de «Reclamar de nuevo»
/// llama a esas mismas dos preparaciones). La misma fecha de vencimiento tiene que dar el mismo veredicto
/// en todas: lo que una ofrece, las otras lo aceptan, y lo que una rechaza no se cuela por otra
/// (#1026, #1028: el literal de 3 meses estaba copiado y la ficha de Trabajador 360 ofrecía lo que el
/// envío rechazaba).
///
/// <para>
/// Excepción deliberada, fuera de esta tabla: la ficha de Trabajador 360 ofrece «Reclamar» solo lo que además
/// no está <see cref="EstadoDocumento.Vigente"/> (un documento a 2-3 meses, vigente por los umbrales, lo
/// reclama el lote pero no el botón de la ficha). Ofrece MENOS que lo que el envío acepta, nunca más, así que
/// no rompe la regla «lo que se ofrece se acepta»; su ventana sale del mismo <c>EsReclamable</c>.
/// </para>
/// </summary>
public class CoherenciaDeLaVentanaDeReclamacionEntreSuperficiesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();

    private Guid _clienteId;
    private Guid _empresaId;

    private sealed record Caso(string Nombre, bool EsReclamable, VigenciaDocumento Vigencia)
    {
        public Guid DocumentoTrabajadorId { get; set; }
        public Guid DocumentoEmpresaId { get; set; }
    }

    private List<Caso> _casos = [];

    public async Task InitializeAsync()
    {
        var limite = VentanaReclamacion.Limite(_hoy);
        // Control: las fechas de la tabla cuelgan del límite de la regla; este control fija el valor de la regla
        // (3 meses, VentanaReclamacionTests lo fija con fechas absolutas) para que la tabla no sea circular.
        limite.Should().Be(_hoy.AddMonths(3), "la ventana de reclamación es de 3 meses");
        _casos =
        [
            new("Vencido hace un año (sin limite inferior)", true, VigenciaDocumento.VenceEl(_hoy.AddYears(-1))),
            new("Vence hoy", true, VigenciaDocumento.VenceEl(_hoy)),
            new("Un dia antes del limite", true, VigenciaDocumento.VenceEl(limite.AddDays(-1))),
            new("Justo en el limite", true, VigenciaDocumento.VenceEl(limite)),
            new("Un dia despues del limite", false, VigenciaDocumento.VenceEl(limite.AddDays(1))),
            new("Sin confirmar (sin fecha)", false, VigenciaDocumento.SinConfirmar),
            new("No caduca (sin fecha)", false, VigenciaDocumento.NoCaduca),
        ];

        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente de la ventana", "B12345674", false, null, null);
        var empresa = new Empresa("Contratista de la ventana S.L.", "B87654323");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro de la ventana");
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ventana", "De Reclamacion", "77189989B");
        contexto.Centros.Add(centro);
        contexto.Trabajadores.Add(trabajador);

        var tipoTrabajador = new TipoDocumento("Ficha de riesgos", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador);
        var tipoEmpresa = new TipoDocumento("Plan de prevencion", null, aplicaVencimientoAutomatico: false, 2, AmbitoAplicacion.Empresa);
        contexto.TiposDocumento.AddRange(tipoTrabajador, tipoEmpresa);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, _hoy.AddDays(-400)));
        contexto.ContactosAgenda.Add(ContactoAgenda.DeCliente(cliente.Id, "cliente@ventana.test", "cliente@ventana.test", esPredeterminado: true));
        contexto.ContactosAgenda.Add(ContactoAgenda.DeEmpresa(empresa.Id, "empresa@ventana.test", "empresa@ventana.test", esPredeterminado: true));

        foreach (var caso in _casos)
        {
            var docTrabajador = Documento.DeTrabajador(trabajador.Id, tipoTrabajador.Id, _hoy.AddDays(-500), caso.Vigencia);
            var docEmpresa = Documento.DeEmpresa(empresa.Id, tipoEmpresa.Id, _hoy.AddDays(-500), caso.Vigencia);
            contexto.Documentos.AddRange(docTrabajador, docEmpresa);
            caso.DocumentoTrabajadorId = docTrabajador.Id;
            caso.DocumentoEmpresaId = docEmpresa.Id;
        }

        await contexto.SaveChangesAsync();
        _clienteId = cliente.Id;
        _empresaId = empresa.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_misma_fecha_de_vencimiento_da_el_mismo_veredicto_en_todas_las_superficies()
    {
        var fallos = new List<string>();

        void Comprobar(string superficie, Caso caso, bool observado)
        {
            if (observado != caso.EsReclamable)
                fallos.Add($"{superficie} · {caso.Nombre}: esperado {(caso.EsReclamable ? "reclamable" : "NO reclamable")}, " +
                           $"observado {(observado ? "reclamable" : "NO reclamable")}");
        }

        await using var c = CrearContexto();
        var alcance = new AlcanceDatosServiceFalso();
        var resolucion = new ResolucionDestinatariosAgendaService(c, c);

        // 1. La regla en memoria (la usa la ficha de Trabajador 360 para ofrecer el botón).
        foreach (var caso in _casos)
            Comprobar("VentanaReclamacion.EsReclamable", caso, VentanaReclamacion.EsReclamable(caso.Vigencia.FechaVencimiento, _hoy));

        // 2-3. La vista previa del lote: de Trabajadores y de Empresas.
        var loteTrabajadores = await new ObtenerLoteReclamacionQueryHandler(c, c, c, c, c, c, c, c, alcance, resolucion)
            .Handle(new ObtenerLoteReclamacionQuery(), CancellationToken.None);
        var idsLoteTrabajadores = loteTrabajadores.SelectMany(l => l.Documentos).Select(d => d.DocumentoId).ToHashSet();
        var loteEmpresas = await new ObtenerLoteReclamacionEmpresaQueryHandler(c, c, c, c, c, alcance, resolucion)
            .Handle(new ObtenerLoteReclamacionEmpresaQuery(), CancellationToken.None);
        var idsLoteEmpresas = loteEmpresas.SelectMany(l => l.Documentos).Select(d => d.DocumentoId).ToHashSet();

        foreach (var caso in _casos)
        {
            Comprobar("Lote de Trabajadores (vista previa)", caso, idsLoteTrabajadores.Contains(caso.DocumentoTrabajadorId));
            Comprobar("Lote de Empresas (vista previa)", caso, idsLoteEmpresas.Contains(caso.DocumentoEmpresaId));
        }

        // 4-5. Lo que el envío acepta (PrepararAsync es el envío sin enviar: es la única implementación de lo
        //      que se acepta, y la vista previa de «Reclamar de nuevo» la llama tal cual).
        var enviarCliente = new EnviarReclamacionCommandHandler(c, c, c, c, c, c, alcance, resolucion, registroEnvio: null!);
        var enviarEmpresa = new EnviarReclamacionEmpresaCommandHandler(c, c, c, alcance, resolucion, registroEnvio: null!);
        foreach (var caso in _casos)
        {
            var alCliente = await enviarCliente.PrepararAsync(
                new EnviarReclamacionCommand(_clienteId, [caso.DocumentoTrabajadorId]), CancellationToken.None);
            Comprobar("Envío al Cliente empresarial (preparación)", caso, alCliente.EsExitoso);

            var aLaEmpresa = await enviarEmpresa.PrepararAsync(
                new EnviarReclamacionEmpresaCommand(_empresaId, [caso.DocumentoEmpresaId]), CancellationToken.None);
            Comprobar("Envío a la Empresa (preparación)", caso, aLaEmpresa.EsExitoso);
        }

        fallos.Should().BeEmpty(
            "la ventana de reclamación es una sola regla; una superficie que la calcule aparte ofrece lo que otra rechaza");
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
