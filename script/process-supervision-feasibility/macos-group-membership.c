#include <errno.h>
#include <libproc.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#define FAILURE_EXIT_CODE 42
#define PROBE_TIMEOUT_SECONDS 5.0

struct membership_snapshot {
    size_t member_count;
    bool contains_expected_descendant;
};

static double monotonic_seconds(void)
{
    struct timespec value;
    if (clock_gettime(CLOCK_MONOTONIC, &value) != 0) {
        return -1.0;
    }
    return (double)value.tv_sec + ((double)value.tv_nsec / 1000000000.0);
}

static void sleep_briefly(void)
{
    struct timespec duration = { .tv_sec = 0, .tv_nsec = 20000000 };
    while (nanosleep(&duration, &duration) != 0 && errno == EINTR) {
    }
}

static int write_exact(int descriptor, const void *buffer, size_t length)
{
    const unsigned char *cursor = buffer;
    while (length > 0) {
        ssize_t written = write(descriptor, cursor, length);
        if (written < 0) {
            if (errno == EINTR) {
                continue;
            }
            return -1;
        }
        cursor += (size_t)written;
        length -= (size_t)written;
    }
    return 0;
}

static int read_exact(int descriptor, void *buffer, size_t length)
{
    unsigned char *cursor = buffer;
    while (length > 0) {
        ssize_t received = read(descriptor, cursor, length);
        if (received == 0) {
            errno = EPIPE;
            return -1;
        }
        if (received < 0) {
            if (errno == EINTR) {
                continue;
            }
            return -1;
        }
        cursor += (size_t)received;
        length -= (size_t)received;
    }
    return 0;
}

static int query_membership(
    pid_t process_group_id,
    pid_t anchor_process_id,
    pid_t expected_descendant,
    bool inject_failure,
    struct membership_snapshot *snapshot)
{
    if (inject_failure) {
        errno = EIO;
        return -1;
    }

    errno = 0;
    int suggested_capacity = proc_listpgrppids(process_group_id, NULL, 0);
    if (suggested_capacity == 0 && errno != 0) {
        return -1;
    }
    if (suggested_capacity < 32) {
        suggested_capacity = 32;
    }

    for (int attempt = 0; attempt < 5; attempt++) {
        int capacity = suggested_capacity << attempt;
        pid_t *process_ids = calloc((size_t)capacity, sizeof(pid_t));
        if (process_ids == NULL) {
            errno = ENOMEM;
            return -1;
        }

        errno = 0;
        int count = proc_listpgrppids(
            process_group_id,
            process_ids,
            capacity * (int)sizeof(pid_t));
        int query_error = errno;
        if (count == 0 && query_error != 0) {
            free(process_ids);
            errno = query_error;
            return -1;
        }
        if (count >= capacity) {
            free(process_ids);
            continue;
        }

        snapshot->member_count = 0;
        snapshot->contains_expected_descendant = false;
        for (int index = 0; index < count; index++) {
            pid_t process_id = process_ids[index];
            if (process_id <= 0 || process_id == anchor_process_id) {
                continue;
            }
            snapshot->member_count++;
            if (process_id == expected_descendant) {
                snapshot->contains_expected_descendant = true;
            }
        }
        free(process_ids);
        return 0;
    }

    errno = EOVERFLOW;
    return -1;
}

static int wait_for_reparent(pid_t descendant_process_id, pid_t former_parent)
{
    double deadline = monotonic_seconds() + PROBE_TIMEOUT_SECONDS;
    while (monotonic_seconds() < deadline) {
        struct proc_bsdinfo information;
        memset(&information, 0, sizeof(information));
        int bytes = proc_pidinfo(
            descendant_process_id,
            PROC_PIDTBSDINFO,
            0,
            &information,
            (int)sizeof(information));
        if (bytes == (int)sizeof(information) &&
            information.pbi_ppid != (uint32_t)former_parent) {
            return 0;
        }
        sleep_briefly();
    }
    errno = ETIMEDOUT;
    return -1;
}

static int wait_for_quiescence(pid_t process_group_id, pid_t anchor_process_id)
{
    double deadline = monotonic_seconds() + PROBE_TIMEOUT_SECONDS;
    while (monotonic_seconds() < deadline) {
        struct membership_snapshot snapshot;
        if (query_membership(
                process_group_id,
                anchor_process_id,
                -1,
                false,
                &snapshot) != 0) {
            return -1;
        }
        if (snapshot.member_count == 0) {
            return 0;
        }
        sleep_briefly();
    }
    errno = ETIMEDOUT;
    return -1;
}

static int run_probe(bool inject_membership_failure)
{
    int anchor_status_pipe[2] = { -1, -1 };
    int descendant_pipe[2] = { -1, -1 };
    pid_t anchor_process_id = -1;
    pid_t root_process_id = -1;
    pid_t descendant_process_id = -1;
    bool root_reaped = false;
    bool anchor_reaped = false;
    bool anchor_group_established = false;
    bool failed_closed = false;
    int result = 1;

    if (pipe(anchor_status_pipe) != 0 || pipe(descendant_pipe) != 0) {
        perror("pipe");
        goto cleanup;
    }

    anchor_process_id = fork();
    if (anchor_process_id < 0) {
        perror("fork anchor");
        goto cleanup;
    }
    if (anchor_process_id == 0) {
        close(anchor_status_pipe[0]);
        close(descendant_pipe[0]);
        close(descendant_pipe[1]);
        int status = 0;
        if (setpgid(0, 0) != 0) {
            status = errno;
        }
        (void)write_exact(anchor_status_pipe[1], &status, sizeof(status));
        close(anchor_status_pipe[1]);
        if (status != 0) {
            _exit(70);
        }
        for (;;) {
            pause();
        }
    }

    close(anchor_status_pipe[1]);
    anchor_status_pipe[1] = -1;
    int anchor_status = 0;
    if (read_exact(anchor_status_pipe[0], &anchor_status, sizeof(anchor_status)) != 0 ||
        anchor_status != 0 || getpgid(anchor_process_id) != anchor_process_id) {
        fprintf(stderr, "Anchor did not establish stable group identity: %d\n", anchor_status);
        goto cleanup;
    }
    close(anchor_status_pipe[0]);
    anchor_status_pipe[0] = -1;
    anchor_group_established = true;

    root_process_id = fork();
    if (root_process_id < 0) {
        perror("fork workload root");
        goto cleanup;
    }
    if (root_process_id == 0) {
        close(descendant_pipe[0]);
        if (setpgid(0, anchor_process_id) != 0) {
            pid_t failure = -(pid_t)errno;
            (void)write_exact(descendant_pipe[1], &failure, sizeof(failure));
            _exit(71);
        }

        pid_t child = fork();
        if (child < 0) {
            pid_t failure = -(pid_t)errno;
            (void)write_exact(descendant_pipe[1], &failure, sizeof(failure));
            _exit(72);
        }
        if (child == 0) {
            close(descendant_pipe[1]);
            for (;;) {
                pause();
            }
        }

        (void)write_exact(descendant_pipe[1], &child, sizeof(child));
        close(descendant_pipe[1]);
        _exit(0);
    }

    close(descendant_pipe[1]);
    descendant_pipe[1] = -1;
    if (read_exact(
            descendant_pipe[0],
            &descendant_process_id,
            sizeof(descendant_process_id)) != 0 ||
        descendant_process_id <= 0) {
        fprintf(stderr, "The workload root did not report a descendant: %d\n",
            (int)descendant_process_id);
        goto cleanup;
    }
    close(descendant_pipe[0]);
    descendant_pipe[0] = -1;

    int root_status = 0;
    if (waitpid(root_process_id, &root_status, 0) != root_process_id ||
        !WIFEXITED(root_status) || WEXITSTATUS(root_status) != 0) {
        fprintf(stderr, "The workload root did not exit cleanly.\n");
        goto cleanup;
    }
    root_reaped = true;

    if (wait_for_reparent(descendant_process_id, root_process_id) != 0) {
        perror("descendant reparent");
        goto cleanup;
    }
    if (kill(descendant_process_id, 0) != 0) {
        perror("live descendant diagnostic");
        goto cleanup;
    }

    struct membership_snapshot active_snapshot;
    if (query_membership(
            anchor_process_id,
            anchor_process_id,
            descendant_process_id,
            inject_membership_failure,
            &active_snapshot) != 0) {
        if (!inject_membership_failure) {
            perror("authoritative membership query");
            goto cleanup;
        }
        failed_closed = true;
    } else if (inject_membership_failure) {
        fprintf(stderr, "Injected membership failure produced success.\n");
        goto cleanup;
    } else if (active_snapshot.member_count == 0 ||
        !active_snapshot.contains_expected_descendant) {
        fprintf(stderr, "The membership backend missed the live descendant.\n");
        goto cleanup;
    }

    if (kill(-anchor_process_id, SIGKILL) != 0) {
        perror("process-group termination");
        goto cleanup;
    }
    if (wait_for_quiescence(anchor_process_id, anchor_process_id) != 0) {
        perror("membership convergence");
        goto cleanup;
    }

    int anchor_wait_status = 0;
    if (waitpid(anchor_process_id, &anchor_wait_status, 0) != anchor_process_id ||
        !WIFSIGNALED(anchor_wait_status) || WTERMSIG(anchor_wait_status) != SIGKILL) {
        fprintf(stderr, "The group anchor was not deterministically reaped.\n");
        goto cleanup;
    }
    anchor_reaped = true;

#if defined(__arm64__)
    const char *architecture = "arm64";
#elif defined(__x86_64__)
    const char *architecture = "x64";
#else
    const char *architecture = "unknown";
#endif

    printf(
        "{\"backend\":\"proc_listpgrppids\",\"architecture\":\"%s\","
        "\"rootExited\":true,\"descendantWasAlive\":true,"
        "\"descendantReparented\":true,\"termination\":\"process-group\","
        "\"quiescent\":true,\"failureInjected\":%s,\"failedClosed\":%s}\n",
        architecture,
        inject_membership_failure ? "true" : "false",
        failed_closed ? "true" : "false");
    result = inject_membership_failure ? FAILURE_EXIT_CODE : 0;

cleanup:
    if (anchor_status_pipe[0] >= 0) {
        close(anchor_status_pipe[0]);
    }
    if (anchor_status_pipe[1] >= 0) {
        close(anchor_status_pipe[1]);
    }
    if (descendant_pipe[0] >= 0) {
        close(descendant_pipe[0]);
    }
    if (descendant_pipe[1] >= 0) {
        close(descendant_pipe[1]);
    }

    if (root_process_id > 0 && !root_reaped) {
        if (anchor_group_established) {
            (void)kill(-anchor_process_id, SIGKILL);
        } else {
            (void)kill(root_process_id, SIGKILL);
        }
        int status = 0;
        if (waitpid(root_process_id, &status, 0) != root_process_id && errno != ECHILD) {
            perror("cleanup root reap");
            result = 1;
        }
        root_reaped = true;
    }
    if (anchor_process_id > 0 && !anchor_reaped) {
        if (anchor_group_established) {
            (void)kill(-anchor_process_id, SIGKILL);
            if (wait_for_quiescence(anchor_process_id, anchor_process_id) != 0) {
                perror("cleanup membership convergence");
                result = 1;
            }
        } else {
            (void)kill(anchor_process_id, SIGKILL);
        }
        int status = 0;
        if (waitpid(anchor_process_id, &status, 0) == anchor_process_id) {
            anchor_reaped = true;
        } else if (errno != ECHILD) {
            perror("cleanup anchor reap");
            result = 1;
        }
    }
    return result;
}

int main(int argument_count, char **arguments)
{
    bool inject_membership_failure = false;
    if (argument_count == 2 &&
        strcmp(arguments[1], "--inject-membership-failure") == 0) {
        inject_membership_failure = true;
    } else if (argument_count != 1) {
        fprintf(stderr, "Usage: %s [--inject-membership-failure]\n", arguments[0]);
        return 2;
    }

    return run_probe(inject_membership_failure);
}
