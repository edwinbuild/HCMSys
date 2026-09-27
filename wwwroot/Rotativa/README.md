# Placeholder folder — Rotativa.AspNetCore requires this to exist at startup for RotativaConfiguration.Setup().
# The actual wkhtmltopdf binary is installed system-wide via the Dockerfile (apt) at /usr/bin/wkhtmltopdf;
# TimeAttendanceController sets that path explicitly on every PDF export (see BuildPdfResult), so nothing
# needs to be placed in this folder itself.
