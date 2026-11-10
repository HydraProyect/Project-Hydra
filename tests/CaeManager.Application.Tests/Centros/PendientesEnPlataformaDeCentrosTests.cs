using CaeManager.Application.Centros;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// Quién cuenta en «qué está pendiente en la plataforma de cada Centro» (<see cref="PendientesEnPlataformaDeCentros"/>).
/// Decisión del 2026-11-08, que sustituye al filtro por actividad que la regla del 2026-10-10 aplicaba también a los
/// documentos de Empresa: un Centro sin Trabajadores asignados es un caso legítimo (puede exigir solo documentos de Empresa),
/// así que el documento de Empresa pendiente cuenta en los Centros de su Empresa (<c>Centro.EmpresaId</c>); el de Trabajador
/// sigue exigiendo una Asignación activa en el Centro. Se prueba el cargador REAL sobre los contextos de consulta falsos.
/// </summary>
public class PendientesEnPlataformaDeCentrosTests
{
    private static readonly DateOnly Hoy = new(2026, 1, 15);

    private static Task<IReadOnlyList<PendienteEnPlataformaDeCentro>> Cargar(MundoAcreditaciones mundo, params Centro[] centros) =>
        PendientesEnPlataformaDeCentros.CargarAsync(
            mundo.CentrosContexto, mundo.DocumentosContexto, mundo.TiposDocumentoContexto, mundo.TrabajadoresContexto,
            mundo.AsignacionesContexto.Asignaciones, centros.Select(c => c.Id).ToList(), Hoy, CancellationToken.None);

    /// <summary>Un Centro de la Empresa dada, con un acceso de plataforma y sin ninguna Asignación.</summary>
    private static (Centro Centro, CanalGestionDocumental Plataforma) CentroConPlataforma(MundoAcreditaciones mundo, Guid empresaDelCentroId)
    {
        var centro = new Centro(Guid.NewGuid(), empresaDelCentroId, "Planta Norte");
        mundo.CentrosContexto.ListaCentros.Add(centro);
        return (centro, mundo.AccesoPlataforma(centro));
    }

    /// <summary>La acreditación del documento ante el acceso, sin subir (el estado con el que nace).</summary>
    private static void AcreditarSinSubir(MundoAcreditaciones mundo, Documento documento, CanalGestionDocumental plataforma) =>
        mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Add(
            new AcreditacionDocumentoPlataforma(documento.Id, plataforma.Id));

    [Fact]
    public async Task Un_documento_de_Empresa_pendiente_cuenta_en_un_Centro_de_su_Empresa_sin_Trabajadores_asignados()
    {
        var mundo = new MundoAcreditaciones();
        var (centro, plataforma) = CentroConPlataforma(mundo, mundo.EmpresaId);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "RNT");
        var documento = mundo.DocumentoDeEmpresa(tipo);
        AcreditarSinSubir(mundo, documento, plataforma);

        var pendientes = await Cargar(mundo, centro);

        pendientes.Should().ContainSingle("el Centro no tiene a nadie asignado, pero exige el RNT de su Empresa")
            .Which.Should().Match<PendienteEnPlataformaDeCentro>(p =>
                p.CentroId == centro.Id && p.EmpresaId == mundo.EmpresaId && p.TrabajadorId == null
                && p.DocumentoId == documento.Id && p.TipoDocumentoId == tipo.Id
                && p.EstadoAcreditacion == EstadoAcreditacion.PendienteDeSubir);
    }

    /// <summary>
    /// Las dos mitades de la decisión del 2026-11-08 juntas, con los servicios REALES: el alta
    /// (<see cref="IAltaAcreditacionesPlataformaService"/>, #2074) acredita al documento de Empresa en el Centro de su
    /// Empresa sin Trabajadores asignados, y el cargador (esta PR) lo cuenta como Pendiente. Cada mitad por separado ya
    /// está probada; esta fija que no se desacoplen (ni el alta con la regla antigua, ni el cargador con el filtro por actividad).
    /// </summary>
    [Fact]
    public async Task El_alta_acredita_un_documento_de_Empresa_en_su_Centro_sin_Trabajadores_y_el_Centro_lo_cuenta_como_Pendiente_y_no_en_el_de_otra_Empresa()
    {
        var mundo = new MundoAcreditaciones();
        var (centroPropio, plataformaPropia) = CentroConPlataforma(mundo, mundo.EmpresaId);
        var (centroAjeno, _) = CentroConPlataforma(mundo, Guid.NewGuid());
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "RNT");
        var documento = mundo.DocumentoDeEmpresa(tipo);

        var agregadas = await mundo.Servicio().AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] });
        mundo.Guardar();

        agregadas.Should().Be(1, "solo el Centro de su Empresa lo exige, aunque nadie esté asignado allí");
        var pendientes = await Cargar(mundo, centroPropio, centroAjeno);
        pendientes.Should().ContainSingle("el Centro de otra Empresa sin Trabajadores suyos no acredita ni cuenta")
            .Which.Should().Match<PendienteEnPlataformaDeCentro>(p =>
                p.CentroId == centroPropio.Id && p.EmpresaId == mundo.EmpresaId && p.TrabajadorId == null
                && p.DocumentoId == documento.Id && p.EstadoAcreditacion == EstadoAcreditacion.PendienteDeSubir);
        mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Should().ContainSingle()
            .Which.CanalGestionDocumentalId.Should().Be(plataformaPropia.Id);
    }

    [Fact]
    public async Task Un_documento_de_Trabajador_pendiente_en_un_Centro_sin_Asignacion_activa_no_cuenta_y_el_de_Empresa_del_mismo_Centro_si()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var (centro, plataforma) = CentroConPlataforma(mundo, mundo.EmpresaId);
        var tipoTrabajador = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Trabajador, "Formación PRL");
        var tipoEmpresa = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "RNT");
        AcreditarSinSubir(mundo, mundo.DocumentoDe(trabajador, tipoTrabajador), plataforma);
        AcreditarSinSubir(mundo, mundo.DocumentoDeEmpresa(tipoEmpresa), plataforma);

        var pendientes = await Cargar(mundo, centro);

        pendientes.Should().ContainSingle("el Trabajador no tiene Asignación activa en el Centro: sin actividad no cuenta")
            .Which.TipoDocumentoId.Should().Be(tipoEmpresa.Id);
    }

    [Fact]
    public async Task Un_documento_de_Trabajador_pendiente_solo_cuenta_con_Asignacion_activa_en_el_Centro()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var (centro, plataforma) = CentroConPlataforma(mundo, mundo.EmpresaId);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Trabajador, "Formación PRL");
        AcreditarSinSubir(mundo, mundo.DocumentoDe(trabajador, tipo), plataforma);
        mundo.Asignacion(trabajador, centro);

        var pendientes = await Cargar(mundo, centro);

        pendientes.Should().ContainSingle("control positivo: con Asignación activa el de Trabajador sigue contando")
            .Which.TrabajadorId.Should().Be(trabajador.Id);
    }

    [Fact]
    public async Task Un_documento_de_Empresa_pendiente_no_cuenta_en_un_Centro_de_otra_Empresa_sin_Trabajadores_suyos()
    {
        var mundo = new MundoAcreditaciones();
        var (centroDeOtraEmpresa, plataforma) = CentroConPlataforma(mundo, Guid.NewGuid());
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "RNT");
        AcreditarSinSubir(mundo, mundo.DocumentoDeEmpresa(tipo), plataforma);

        var pendientes = await Cargar(mundo, centroDeOtraEmpresa);

        pendientes.Should().BeEmpty("el Centro es de otra Empresa y esta no tiene a nadie asignado allí");
    }

    [Fact]
    public async Task Un_documento_de_Empresa_pendiente_sigue_contando_en_el_Centro_de_otra_Empresa_donde_tiene_un_Trabajador_asignado()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var (centroDeOtraEmpresa, plataforma) = CentroConPlataforma(mundo, Guid.NewGuid());
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "RNT");
        AcreditarSinSubir(mundo, mundo.DocumentoDeEmpresa(tipo), plataforma);
        mundo.Asignacion(trabajador, centroDeOtraEmpresa);

        var pendientes = await Cargar(mundo, centroDeOtraEmpresa);

        pendientes.Should().ContainSingle("la regla del 2026-10-10 se mantiene: la Empresa con un Trabajador suyo asignado cuenta en cualquier Centro")
            .Which.EmpresaId.Should().Be(mundo.EmpresaId);
    }

    [Fact]
    public async Task Un_documento_de_una_Subcontrata_subido_sin_validar_cuenta_en_el_Centro_donde_tiene_un_Trabajador_asignado_y_no_donde_no()
    {
        var mundo = new MundoAcreditaciones();
        var subcontrataId = Guid.NewGuid();
        var trabajador = Domain.Trabajadores.Trabajador.DeSubcontrata(subcontrataId, "Luis", "Gómez", "87654321X");
        mundo.TrabajadoresContexto.ListaTrabajadores.Add(trabajador);
        var (centroConTrabajador, plataformaCon) = CentroConPlataforma(mundo, Guid.NewGuid());
        var (centroSinTrabajador, plataformaSin) = CentroConPlataforma(mundo, Guid.NewGuid());
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "RNT");
        var documento = Documento.DeEmpresa(subcontrataId, tipo.Id, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(5)));
        mundo.DocumentosContexto.ListaDocumentos.Add(documento);
        foreach (var plataforma in new[] { plataformaCon, plataformaSin })
        {
            var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, plataforma.Id);
            acreditacion.MarcarSubida();
            mundo.DocumentosContexto.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
        }
        mundo.Asignacion(trabajador, centroConTrabajador);

        var pendientes = await Cargar(mundo, centroConTrabajador, centroSinTrabajador);

        pendientes.Should().ContainSingle("solo el Centro donde la Subcontrata tiene un Trabajador asignado (su Empresa es la Subcontrata)")
            .Which.Should().Match<PendienteEnPlataformaDeCentro>(p =>
                p.CentroId == centroConTrabajador.Id && p.EmpresaId == subcontrataId
                && p.EstadoAcreditacion == EstadoAcreditacion.Subida);
    }

    [Fact]
    public async Task Un_documento_de_Empresa_pendiente_no_cuenta_en_su_Centro_si_el_Centro_no_exige_el_tipo()
    {
        var mundo = new MundoAcreditaciones();
        var (centro, plataforma) = CentroConPlataforma(mundo, mundo.EmpresaId);
        var tipo = mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "RNT");
        mundo.Requisito(tipo, centro, incluido: false);
        AcreditarSinSubir(mundo, mundo.DocumentoDeEmpresa(tipo), plataforma);

        var pendientes = await Cargar(mundo, centro);

        pendientes.Should().BeEmpty("la acreditación sobrevive a que el Centro deje de exigir el tipo, pero no es trabajo pendiente");
    }
}
