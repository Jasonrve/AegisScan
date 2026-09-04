package operator

import (
	"context"
	"fmt"
	batchv1 "k8s.io/api/batch/v1"
	corev1 "k8s.io/api/core/v1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/labels"
	"k8s.io/client-go/kubernetes"
	"k8s.io/client-go/rest"
	"k8s.io/client-go/tools/clientcmd"
	"path/filepath"
)

type KubernetesJobClient struct {
	Client    kubernetes.Interface
	Namespace string
}

func NewRemoteKubernetesJobClient(server, token, namespace string) (*KubernetesJobClient, error) {
	config := &rest.Config{Host: server, BearerToken: token, TLSClientConfig: rest.TLSClientConfig{Insecure: true}}
	client, err := kubernetes.NewForConfig(config)
	if err != nil {
		return nil, err
	}
	if namespace == "" {
		namespace = "default"
	}
	return &KubernetesJobClient{Client: client, Namespace: namespace}, nil
}

func NewKubernetesJobClient(kubeconfig, namespace string) (*KubernetesJobClient, error) {
	var config *rest.Config
	var err error
	if kubeconfig != "" {
		config, err = clientcmd.BuildConfigFromFlags("", filepath.Clean(kubeconfig))
	} else {
		config, err = rest.InClusterConfig()
	}
	if err != nil {
		return nil, fmt.Errorf("load kubernetes config: %w", err)
	}
	client, err := kubernetes.NewForConfig(config)
	if err != nil {
		return nil, err
	}
	if namespace == "" {
		namespace = "aegisscan"
	}
	return &KubernetesJobClient{Client: client, Namespace: namespace}, nil
}

func (c *KubernetesJobClient) Create(ctx context.Context, manifest JobManifest) error {
	job := &batchv1.Job{ObjectMeta: metav1.ObjectMeta{Name: manifest.Metadata.Name, Namespace: c.namespace(manifest.Metadata.Namespace), Labels: manifest.Metadata.Labels}, Spec: batchv1.JobSpec{BackoffLimit: int32ptr(int32(manifest.Spec.BackoffLimit)), TTLSecondsAfterFinished: int32ptr(int32(manifest.Spec.TTLSecondsAfterFinished)), Template: corePod(manifest.Spec.Template)}}
	_, err := c.Client.BatchV1().Jobs(job.Namespace).Create(ctx, job, metav1.CreateOptions{})
	return err
}
func (c *KubernetesJobClient) Delete(ctx context.Context, name string) error {
	return c.Client.BatchV1().Jobs(c.Namespace).Delete(ctx, name, metav1.DeleteOptions{})
}
func (c *KubernetesJobClient) List(ctx context.Context, filter map[string]string) ([]JobRecord, error) {
	list, err := c.Client.BatchV1().Jobs(c.Namespace).List(ctx, metav1.ListOptions{LabelSelector: labels.Set(filter).String()})
	if err != nil {
		return nil, err
	}
	out := make([]JobRecord, 0, len(list.Items))
	for _, j := range list.Items {
		out = append(out, JobRecord{Name: j.Name, CreatedAt: j.CreationTimestamp.Time, Active: j.Status.Active > 0})
	}
	return out, nil
}
func (c *KubernetesJobClient) Status(ctx context.Context, name string) (string, error) {
	j, err := c.Client.BatchV1().Jobs(c.Namespace).Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return "", err
	}
	if j.Status.Succeeded > 0 {
		return "succeeded", nil
	}
	if j.Status.Failed > 0 {
		return "failed", nil
	}
	return "running", nil
}
func (c *KubernetesJobClient) Logs(ctx context.Context, name string) (string, error) {
	pods, err := c.Client.CoreV1().Pods(c.Namespace).List(ctx, metav1.ListOptions{LabelSelector: "job-name=" + name})
	if err != nil || len(pods.Items) == 0 {
		return "", err
	}
	stream, err := c.Client.CoreV1().Pods(c.Namespace).GetLogs(pods.Items[0].Name, &corev1.PodLogOptions{Container: "scanner"}).Stream(ctx)
	if err != nil {
		return "", err
	}
	defer stream.Close()
	b := make([]byte, 256*1024)
	n, _ := stream.Read(b)
	return string(b[:n]), nil
}
func (c *KubernetesJobClient) namespace(value string) string {
	if value != "" {
		return value
	}
	return c.Namespace
}
func int32ptr(v int32) *int32 { return &v }

func corePod(p PodTemplate) corev1.PodTemplateSpec {
	containers := make([]corev1.Container, 0, len(p.Spec.Containers))
	for _, c := range p.Spec.Containers {
		containers = append(containers, corev1.Container{Name: c.Name, Image: c.Image, Command: c.Command, Args: c.Args})
	}
	return corev1.PodTemplateSpec{ObjectMeta: metav1.ObjectMeta{Labels: p.Metadata.Labels}, Spec: corev1.PodSpec{RestartPolicy: corev1.RestartPolicy(p.Spec.RestartPolicy), Containers: containers}}
}
