using CaeManager.Domain.Common;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Application.Documentos.SituacionEnCentro;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Trabajadores;

/// <summary>
/// Pestaña Operación de Trabajador 360. El índice (TrabajadorId, TipoDocumentoId)
/// de Documento no es único y CrearDocumento no rechaza un segundo documento del
/// mismo tipo (la renovación típica: el vencido sigue ahí y se sube el nuevo), así
/// que la Query debe elegir UN documento por tipo en vez de fallar.
/// </summary>
public class ObtenerDocumentacionPorCentroDeTrabajadorQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _trabajadorId;
    private Guid _tipoId;
    private Guid _centroId;
    private Guid _clienteEmpresarialId;
    private Guid _empresaId;
    private Guid _subcontrataId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente Trabajador 360 S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Trabajador 360 S.L.", "B87654323");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Trabajador 360");
        contexto.Centros.Add(centro);

        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Trabajador 360 S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        contexto.Empresas.Add(subcontrata);

        var tipo = new TipoDocumento("Formación PRL", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var trabajador = Trabajador.DeSubcontrata(subcontrata.Id, "Rosa", "Renovada", "12345678Z");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DiaDeNegocio.Hoy()));
        await contexto.SaveChangesAsync();

        _trabajadorId = trabajador.Id;
        _tipoId = tipo.Id;
        _centroId = centro.Id;
        _clienteEmpresarialId = cliente.Id;
        _empresaId = empresa.Id;
        _subcontrataId = subcontrata.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Con_el_vencido_y_su_renovacion_del_mismo_tipo_representa_al_tipo_el_vigente()
    {
        var hoy = DiaDeNegocio.Hoy();
        Guid renovacionId;
        await using (var contexto = CrearContexto())
        {
            // La renovación se inserta primero y el vencido después, para que
            // «el último leído» no coincida por casualidad con el correcto.
            var renovacion = Documento.DeTrabajador(_trabajadorId, _tipoId, hoy.AddDays(-1), VigenciaDocumento.VenceEl(hoy.AddYears(1)));
            var vencido = Documento.DeTrabajador(_trabajadorId, _tipoId, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddDays(-10)));
            contexto.Documentos.Add(renovacion);
            await contexto.SaveChangesAsync();
            contexto.Documentos.Add(vencido);
            await contexto.SaveChangesAsync();
            renovacionId = renovacion.Id;
        }

        var resultado = await EjecutarAsync();

        var centro = resultado.Should().ContainSingle().Subject;
        var documento = centro.Documentos.Should().ContainSingle(d => d.TipoDocumentoId == _tipoId).Subject;
        documento.DocumentoId.Should().Be(renovacionId);
        documento.Estado.Should().Be(EstadoDocumento.Vigente);
        centro.PeorEstado.Should().Be(EstadoDocumento.Vigente);
    }

    [Fact]
    public async Task Con_dos_vencidos_del_mismo_tipo_representa_al_tipo_el_mas_reciente_y_se_ve_vencido()
    {
        var hoy = DiaDeNegocio.Hoy();
        Guid masRecienteId;
        await using (var contexto = CrearContexto())
        {
            var masReciente = Documento.DeTrabajador(_trabajadorId, _tipoId, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-5)));
            var antiguo = Documento.DeTrabajador(_trabajadorId, _tipoId, hoy.AddYears(-3), VigenciaDocumento.VenceEl(hoy.AddYears(-2)));
            contexto.Documentos.Add(masReciente);
            await contexto.SaveChangesAsync();
            contexto.Documentos.Add(antiguo);
            await contexto.SaveChangesAsync();
            masRecienteId = masReciente.Id;
        }

        var resultado = await EjecutarAsync();

        var centro = resultado.Should().ContainSingle().Subject;
        var documento = centro.Documentos.Should().ContainSingle(d => d.TipoDocumentoId == _tipoId).Subject;
        documento.DocumentoId.Should().Be(masRecienteId);
        documento.Estado.Should().Be(EstadoDocumento.Vencido);
        documento.FechaVencimiento.Should().Be(hoy.AddDays(-5));
    }

    [Fact]
    public async Task Cada_grupo_trae_el_estado_del_documento_en_la_plataforma_de_SU_Centro_y_la_ultima_reclamacion()
    {
        var hoy = DiaDeNegocio.Hoy();
        var primerEnvio = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
        var ultimoEnvio = new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);
        Guid documentoId, otroCentroId;
        await using (var contexto = CrearContexto())
        {
            var documento = Documento.DeTrabajador(_trabajadorId, _tipoId, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddDays(-10)));
            var otroCentro = new Centro(_clienteEmpresarialId, _empresaId, "Otro Centro Trabajador 360");
            var proveedor = new ProveedorPlataformaCae($"prueba-{Guid.NewGuid():N}", "Portal de prueba");
            contexto.Documentos.Add(documento);
            contexto.Centros.Add(otroCentro);
            contexto.ProveedoresPlataformaCae.Add(proveedor);
            await contexto.SaveChangesAsync();
            contexto.Asignaciones.Add(new Asignacion(_trabajadorId, otroCentro.Id, hoy));

            var canalDelCentro = CanalGestionDocumental.DePlataforma(_centroId, "Portal", proveedor.Id, null, null, null);
            var canalDelOtroCentro = CanalGestionDocumental.DePlataforma(otroCentro.Id, "Portal", proveedor.Id, null, null, null);
            contexto.CanalesGestionDocumental.AddRange(canalDelCentro, canalDelOtroCentro);
            await contexto.SaveChangesAsync();

            var subidaEnElCentro = new AcreditacionDocumentoPlataforma(documento.Id, canalDelCentro.Id);
            subidaEnElCentro.MarcarSubida();
            contexto.AcreditacionesDocumentoPlataforma.AddRange(
                subidaEnElCentro, new AcreditacionDocumentoPlataforma(documento.Id, canalDelOtroCentro.Id));

            // Dos envíos, el más reciente insertado primero: manda la fecha, no el orden de lectura.
            contexto.ReclamacionesDocumentales.Add(ReclamacionDocumental.ParaCliente(
                _clienteEmpresarialId, Guid.NewGuid(), "cliente@ejemplo.com", ultimoEnvio, [documento.Id]));
            contexto.ReclamacionesDocumentales.Add(ReclamacionDocumental.ParaCliente(
                _clienteEmpresarialId, Guid.NewGuid(), "cliente@ejemplo.com", primerEnvio, [documento.Id]));
            await contexto.SaveChangesAsync();

            documentoId = documento.Id;
            otroCentroId = otroCentro.Id;
        }

        var resultado = await EjecutarAsync();

        var enElCentro = resultado.Single(c => c.CentroId == _centroId).Documentos.Single(d => d.DocumentoId == documentoId);
        var enElOtro = resultado.Single(c => c.CentroId == otroCentroId).Documentos.Single(d => d.DocumentoId == documentoId);

        var acreditacionDelCentro = enElCentro.AcreditacionesEnElCentro.Should().ContainSingle("solo cuenta el canal de ESE Centro").Subject;
        acreditacionDelCentro.Estado.Should().Be(EstadoAcreditacion.Subida);
        acreditacionDelCentro.NombrePlataforma.Should().Be("Portal de prueba");
        enElOtro.AcreditacionesEnElCentro.Should().ContainSingle().Which.Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);

        enElCentro.UltimaReclamacion.Should().Be(new UltimaReclamacionDocumentoDto(ultimoEnvio, SinRespuesta: null),
            "es la última de las dos, y sin Conversación no se sabe si contestaron");
        enElOtro.UltimaReclamacion.Should().Be(enElCentro.UltimaReclamacion, "la reclamación es del documento, no del Centro");
    }

    [Fact]
    public async Task Un_documento_sin_plataforma_ni_reclamacion_no_trae_nada_para_la_segunda_linea()
    {
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(_trabajadorId, _tipoId, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddDays(-10))));
            await contexto.SaveChangesAsync();
        }

        var documento = (await EjecutarAsync()).Single().Documentos.Single();

        documento.AcreditacionesEnElCentro.Should().BeNull();
        documento.UltimaReclamacion.Should().BeNull();
    }

    [Fact]
    public async Task El_documento_que_falta_trae_la_ultima_vez_que_se_pidio_a_ese_Trabajador()
    {
        var envio = new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);
        await using (var contexto = CrearContexto())
        {
            var otroTrabajador = Trabajador.DeSubcontrata(_subcontrataId, "Otro", "Trabajador", "87654321X");
            contexto.Trabajadores.Add(otroTrabajador);
            await contexto.SaveChangesAsync();

            contexto.ReclamacionesDocumentales.Add(ReclamacionDocumental.ParaCliente(
                _clienteEmpresarialId, Guid.NewGuid(), "cliente@ejemplo.com", envio, [],
                documentosQueFaltan: [new DocumentoQueFaltaPedido(_tipoId, _trabajadorId)]));
            // Mismo tipo pedido MÁS TARDE a otro Trabajador: no es la reclamación de esta fila.
            contexto.ReclamacionesDocumentales.Add(ReclamacionDocumental.ParaCliente(
                _clienteEmpresarialId, Guid.NewGuid(), "cliente@ejemplo.com", envio.AddDays(3), [],
                documentosQueFaltan: [new DocumentoQueFaltaPedido(_tipoId, otroTrabajador.Id)]));
            await contexto.SaveChangesAsync();
        }

        var falta = (await EjecutarAsync()).Single().Documentos.Single();

        falta.DocumentoId.Should().BeNull("control: la fila es un documento que falta");
        falta.UltimaReclamacion.Should().Be(new UltimaReclamacionDocumentoDto(envio, SinRespuesta: null));
    }

    [Fact]
    public async Task La_reclamacion_a_un_titular_que_el_usuario_no_gestiona_no_se_ensena_aunque_vea_el_documento()
    {
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = CrearContexto())
        {
            var documento = Documento.DeTrabajador(_trabajadorId, _tipoId, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddDays(-10)));
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();
            contexto.ReclamacionesDocumentales.Add(ReclamacionDocumental.ParaCliente(
                _clienteEmpresarialId, Guid.NewGuid(), "cliente@ejemplo.com", new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc), [documento.Id]));
            await contexto.SaveChangesAsync();
        }

        // Control positivo: con ese Cliente empresarial entre los titulares que gestiona, la reclamación sí llega.
        var conAlcance = await EjecutarAsync(new AlcanceDatosServiceFalso(clienteIds: [_clienteEmpresarialId], empresaIds: []));
        conAlcance.Single().Documentos.Single().UltimaReclamacion.Should().NotBeNull();

        var sinAlcance = await EjecutarAsync(new AlcanceDatosServiceFalso(clienteIds: [Guid.NewGuid()], empresaIds: []));
        var documentoVisto = sinAlcance.Single().Documentos.Single();
        documentoVisto.DocumentoId.Should().NotBeNull("el documento se sigue viendo");
        documentoVisto.UltimaReclamacion.Should().BeNull();
    }

    /// <param name="alcanceDeReclamaciones">Alcance con el que se acotan las reclamaciones; el handler ve siempre al Trabajador.</param>
    private async Task<IReadOnlyList<CentroDocumentacionTrabajadorDto>> EjecutarAsync(AlcanceDatosServiceFalso? alcanceDeReclamaciones = null)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler(
            contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso(),
            new SituacionDocumentosEnCentrosService(
                contexto, contexto, contexto, contexto, contexto, alcanceDeReclamaciones ?? new AlcanceDatosServiceFalso()));

        return await handler.Handle(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(_trabajadorId), CancellationToken.None);
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
