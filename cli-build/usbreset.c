#include <stdio.h>
#include <libusb-1.0/libusb.h>

int main(void) {
    libusb_context *ctx = NULL;
    int r = libusb_init(&ctx);
    if (r < 0) { printf("libusb_init failed: %s\n", libusb_error_name(r)); return 1; }

    libusb_device_handle *h = libusb_open_device_with_vid_pid(ctx, 0x2A0E, 0x002A);
    if (!h) { printf("device not found (VID 2A0E PID 002A)\n"); libusb_exit(ctx); return 1; }

    printf("device opened, issuing reset...\n");
    r = libusb_reset_device(h);
    printf("libusb_reset_device -> %d (%s)\n", r, libusb_error_name(r));

    libusb_close(h);
    libusb_exit(ctx);
    return 0;
}
