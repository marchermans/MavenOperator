package io.mavenoperator.fixture;

/**
 * Trivial class — just needs to be compilable and deployable.
 * The E2E test verifies the .jar can be uploaded to and downloaded from the operator-managed repo.
 */
public final class Greeting {
    private Greeting() {}

    public static String hello() {
        return "Hello from the Maven Operator E2E fixture!";
    }
}

