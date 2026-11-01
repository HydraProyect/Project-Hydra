namespace CaeManager.Application.Exportacion;

/// <summary>
/// Tercera barrera de la exportación completa, detrás de la condición explícita por Tenant de la lectura y de RLS: cada fila
/// leída tiene que ser del Tenant propietario pedido. Si una sola no lo es, la exportación se
/// aborta entera en vez de omitirla en silencio — que llegara ya significa que las otras dos
/// barreras fallaron, y el zip a medio escribir no se entrega.
/// </summary>
public static class GuardaTenantExportacion
{
    public static void Exigir(Guid tenantPedido, Guid tenantDeLaFila, string entidad)
    {
        if (tenantDeLaFila != tenantPedido)
            throw new InvalidOperationException(
                $"Exportación abortada: una fila de {entidad} no es del Tenant propietario pedido.");
    }
}
