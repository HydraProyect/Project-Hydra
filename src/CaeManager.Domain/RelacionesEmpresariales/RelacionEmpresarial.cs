using CaeManager.Domain.Common;

namespace CaeManager.Domain.RelacionesEmpresariales;

/// <summary>
/// Arista dirigida de prestación de servicio entre dos Empresas del mismo
/// tenant (ADR-011 § 2.4). Sustituye a <c>EmpresaCliente</c>,
/// <c>SubcontrataEmpresa</c> y <c>SubcontrataCliente</c> — ver
/// <c>f4-diseno-fisico-relacionempresarial-2026-08-26.md</c> en el
/// repositorio de negocio para el diseño completo y su revisión adversaria.
///
/// <b>Append-only, igual que <c>AsignacionResponsabilidad</c></b>: no hay
/// ningún método que cambie <see cref="ProveedoraId"/>, <see cref="ClienteId"/>
/// o <see cref="EnmarcadaEnId"/> in situ — cambiar cualquiera de los tres es
/// cerrar esta fila (<see cref="Cerrar"/>) y abrir otra.
///
/// <b>La aciclicidad de <see cref="EnmarcadaEnId"/> NO está garantizada por
/// este tipo ni por el esquema físico</b> — demostrado experimentalmente
/// (revisión adversaria del 2026-08-26: un ciclo de 2 pasos pasa los dos
/// `CHECK` de autorreferencia sin problema). La garantía vive en
/// <c>IRelacionEmpresarialRepository.CreariaUnCicloAsync</c> más el comando que
/// reencuadra una relación — nunca asumir que construir esta entidad
/// basta para excluir un ciclo. Ojo: en un ALTA el chequeo es vacuo por
/// construcción (la fila nueva aún no tiene Id que la cadena pueda alcanzar);
/// solo un reencuadre posterior puede cerrar un ciclo, y es ahí donde el
/// chequeo tiene que ejecutarse.
/// </summary>
public class RelacionEmpresarial : EntidadConTenant
{
    public Guid ProveedoraId { get; private set; }
    public Guid ClienteId { get; private set; }

    /// <summary>Relación padre de la que esta es subcontratación. Null = relación de primer nivel.</summary>
    public Guid? EnmarcadaEnId { get; private set; }

    public DateTime VigenciaDesde { get; private set; }
    public DateTime? VigenciaHasta { get; private set; }
    public OrigenVigencia OrigenVigencia { get; private set; }
    public DateTime CreadoEnUtc { get; private set; }

    public bool EstaVigente => VigenciaHasta is null;

    private RelacionEmpresarial()
    {
        // Requerido por EF Core.
    }

    private RelacionEmpresarial(
        Guid proveedoraId,
        Guid clienteId,
        Guid? enmarcadaEnId,
        DateTime vigenciaDesde,
        OrigenVigencia origenVigencia,
        DateTime ahora)
    {
        if (proveedoraId == Guid.Empty)
            throw new ArgumentException("La relación debe tener una Empresa proveedora.", nameof(proveedoraId));
        if (clienteId == Guid.Empty)
            throw new ArgumentException("La relación debe tener una Empresa cliente.", nameof(clienteId));
        if (proveedoraId == clienteId)
            throw new ArgumentException("Una Empresa no puede tener una relación consigo misma.", nameof(clienteId));

        ProveedoraId = proveedoraId;
        ClienteId = clienteId;
        EnmarcadaEnId = enmarcadaEnId;
        VigenciaDesde = vigenciaDesde;
        OrigenVigencia = origenVigencia;
        CreadoEnUtc = ahora;

        // Defensa en profundidad: en la práctica Id se genera antes de que el
        // llamador pueda conocerlo, así que esto nunca dispara desde código
        // real — el CHECK físico (CK_RelacionesEmpresariales_NoEnmarcadaEnSiMisma)
        // es la garantía real. Se deja aquí para que un test unitario pueda
        // ejercitarlo sin montar base de datos.
        if (EnmarcadaEnId == Id)
            throw new ArgumentException("Una relación no puede estar enmarcada en sí misma.", nameof(enmarcadaEnId));
    }

    /// <summary>
    /// Alta de una relación nueva, con fecha de inicio real conocida —
    /// <see cref="RelacionesEmpresariales.OrigenVigencia.HistoricaConfirmada"/>.
    /// Uso: doble escritura de los comandos de Empresa/Subcontrata mientras
    /// las tres tablas legacy sigan siendo la fuente de escritura primaria
    /// (ver § "doble escritura" del PR), y cualquier alta nueva posterior a
    /// la retirada de legacy.
    /// </summary>
    public static RelacionEmpresarial Crear(
        Guid proveedoraId, Guid clienteId, DateTime ahora, Guid? enmarcadaEnId = null) =>
        new(proveedoraId, clienteId, enmarcadaEnId, vigenciaDesde: ahora, OrigenVigencia.HistoricaConfirmada, ahora);

    /// <summary>
    /// Alta de una relación migrada desde una de las tres tablas legacy —
    /// <see cref="RelacionesEmpresariales.OrigenVigencia.InferidaPorMigracion"/>
    /// siempre, porque la fuente no registraba ninguna fecha real (ADR-011
    /// § 17). <paramref name="vigenciaDesde"/> es la fecha de alta conocida
    /// de la contraparte (nunca de la Empresa propia) — cota de referencia
    /// del sistema, nunca un hecho contractual.
    /// </summary>
    public static RelacionEmpresarial Migrar(
        Guid proveedoraId, Guid clienteId, DateTime vigenciaDesde, DateTime ahora, Guid? enmarcadaEnId = null) =>
        new(proveedoraId, clienteId, enmarcadaEnId, vigenciaDesde, OrigenVigencia.InferidaPorMigracion, ahora);

    /// <summary>
    /// Fin de esta relación — nunca se reabre ni se edita in situ; volver a
    /// operar el mismo par es una fila nueva (<see cref="Crear"/>).
    /// </summary>
    public void Cerrar(DateTime ahora)
    {
        if (VigenciaHasta is not null)
            throw new InvalidOperationException("La relación ya estaba cerrada.");

        VigenciaHasta = ahora > VigenciaDesde ? ahora : VigenciaDesde;
    }
}
