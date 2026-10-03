// Foco al primer error de un formulario de Drawer (DrawerFormulario, S12).
// Tras un «Guardar» que deja errores por campo, el primero puede quedar fuera de
// la vista del cuerpo desplazable (D-20: el aviso salía arriba y con el cuerpo
// desplazado no se veía). Enfocarlo lo trae a la vista y lo anuncia a la
// tecnología de apoyo; sin JS el formulario sigue funcionando, solo sin este salto.
// El error lo marca cada componente de campo (CampoTexto, CampoTextarea, SelectorEntidad,
// SelectorMultiple…) con .campo-mensaje-error y, los que tienen caja, .campo-input-error.
const SELECTOR_ERROR = '.campo-mensaje-error, .campo-input-error, [aria-invalid="true"]';
const SELECTOR_ENFOCABLE = 'input:not([disabled]):not([type="hidden"]), select:not([disabled]), textarea:not([disabled]), button:not([disabled]), [tabindex]:not([tabindex="-1"])';

export function enfocarPrimerError(contenedor) {
    const marca = contenedor?.querySelector(SELECTOR_ERROR);
    if (!marca) {
        return false;
    }

    const campo = marca.matches(SELECTOR_ENFOCABLE)
        ? marca
        : (marca.closest('.campo') ?? marca.parentElement)?.querySelector(SELECTOR_ENFOCABLE);
    if (campo instanceof HTMLElement) {
        campo.focus();
        return true;
    }

    marca.scrollIntoView?.({ block: 'center' });
    return false;
}
