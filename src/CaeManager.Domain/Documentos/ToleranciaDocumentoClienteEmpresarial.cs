using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Tolerancia por defecto de un Cliente empresarial para un Tipo de documento: los días que el documento sigue
/// valiendo para ACCEDER a los Centros de ese Cliente empresarial tras su vencimiento efectivo
/// (<see cref="ReglaBloqueoDeAcceso"/>). Es lo que normalmente fija el Cliente empresarial; cada Centro puede
/// personalizarla en <see cref="TipoDocumentoCentro.ToleranciaDias"/> (<c>null</c> = hereda esta). Resolución:
/// la del Centro si existe; si no, esta; si no, 0 (<see cref="ReglaBloqueoDeAcceso.ResolverToleranciaDias"/>).
///
/// <para>
/// Fila dispersa, mismo patrón que <see cref="ConfiguracionIaDocumentoCliente"/>: sin fila para un
/// (Cliente empresarial, Tipo) la tolerancia es 0. <see cref="ClienteEmpresarialId"/> es el
/// <c>Empresa.Id</c> de la contraparte que recibe el servicio, el mismo que referencia el Centro (<c>Centro.ClienteId</c>,
/// nombre heredado del modelo anterior): aquí se nombra con su concepto canónico y no con el legado.
/// </para>
/// </summary>
public class ToleranciaDocumentoClienteEmpresarial : EntidadConTenant
{
    public Guid ClienteEmpresarialId { get; private set; }
    public Guid TipoDocumentoId { get; private set; }
    public int ToleranciaDias { get; private set; }

    private ToleranciaDocumentoClienteEmpresarial()
    {
    }

    public ToleranciaDocumentoClienteEmpresarial(Guid clienteEmpresarialId, Guid tipoDocumentoId, int toleranciaDias)
    {
        if (clienteEmpresarialId == Guid.Empty)
            throw new ArgumentException("La tolerancia debe pertenecer a un Cliente empresarial.", nameof(clienteEmpresarialId));
        if (tipoDocumentoId == Guid.Empty)
            throw new ArgumentException("La tolerancia debe ser de un tipo de documento.", nameof(tipoDocumentoId));

        ClienteEmpresarialId = clienteEmpresarialId;
        TipoDocumentoId = tipoDocumentoId;
        Establecer(toleranciaDias);
    }

    public void Establecer(int toleranciaDias)
    {
        if (toleranciaDias is < 0 or > TipoDocumentoCentro.ToleranciaMaximaDias)
            throw new ArgumentException(
                $"La tolerancia debe ser un número entero de días entre 0 y {TipoDocumentoCentro.ToleranciaMaximaDias}.",
                nameof(toleranciaDias));

        ToleranciaDias = toleranciaDias;
    }
}
